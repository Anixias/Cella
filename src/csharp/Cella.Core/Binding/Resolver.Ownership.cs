using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public IResolvedExpressionNode Visit(OwnExpressionNode node)
	{
		var isPlace = IsPlaceSyntax(node.Value) ||
		              node.Value is BinaryOpExpressionNode binary && IsAssignment(binary.Op.Type);
		
		var value = VisitNode(node.Value, isPlace ? null : CurrentTargetType);
		if (IsInvalid(value))
			return value;
		
		if (value.Type is DynType dyn)
			return Error(node, $"Cannot use '{dyn.Name}' by value", CurrentTargetType);
		
		if (isPlace && value.Type is not UntypedType)
			return new ResolvedOwnExpressionNode(value, node);
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.Value.SourceLocation,
			"Cannot move unstored values"));
		
		return value;
	}
	
	private bool IsOwnedOutright(IResolvedExpressionNode place) => place switch
	{
		ResolvedVarExpressionNode { Symbol: LocalVariableSymbol { IsBorrowBinding: false, IsLoopBinding: false } } or
			ResolvedVarExpressionNode { Symbol: ParameterSymbol { Mode: ParameterMode.Value } } => true,
		_ when IsDestructorSelf(place) => true,
		ResolvedAccessExpressionNode { Member: FieldSymbol } node => IsOwnedOutright(node.Target) &&
		                                                             (IsConsumedSelf(node.Target) ||
		                                                              node.Target.Type is not RecordSymbol
		                                                              {
			                                                              HasDestructor: true
		                                                              }),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } node =>
			_evaluator.Evaluate(node.Index) is IntegerConstant && IsOwnedOutright(node.Target),
		ResolvedAssignmentExpressionNode node => IsOwnedOutright(node.Left),
		_ => false
	};
	
	private static bool IsConsumedSelf(IResolvedExpressionNode node) => IsDestructorSelf(node) ||
	                                                                    node is ResolvedVarExpressionNode
	                                                                    {
		                                                                    Symbol: ParameterSymbol
		                                                                    {
			                                                                    Mode: ParameterMode.Value,
			                                                                    Name: "self"
		                                                                    }
	                                                                    };
	
	private static bool IsDestructorSelf(IResolvedExpressionNode node) => node is
		ResolvedVarExpressionNode { Symbol: ParameterSymbol { IsDestructorSelf: true } } or
		ResolvedUnaryOpExpressionNode
		{
			Operation.Op: TokenType.OpStar,
			Operand: ResolvedVarExpressionNode { Symbol: ParameterSymbol { IsDestructorSelf: true } }
		};
	
	private static bool IsPlaceSyntax(IExpressionNode node) =>
		node is VarExpressionNode or AccessExpressionNode or IndexerExpressionNode
			or UnaryOpExpressionNode { Op.Type: TokenType.OpStar };
	
	public IResolvedExpressionNode Visit(AtomicExpressionNode node)
	{
		if (node.Place is not UnaryOpExpressionNode { Operand: var operand })
			return node.Ordering == AtomicOrdering.Relaxed
				? Error(node, "Cannot use 'relaxed' on fences", null, node.OrderingLocation!.Value)
				: new ResolvedAtomicExpressionNode(AtomicAccess.Fence, node.Ordering, null, null, null, null,
					NativeSymbols.Void, node);
		
		var resolved = VisitNode(operand, null);
		var pointer = Decay(resolved.Type is UntypedType ? MaterializeAsDefault(resolved) : resolved);
		if (IsInvalid(pointer))
			return RejectAtomic(node, new ResolvedInvalidExpressionNode(node));
		
		if (pointer.Type is not PointerType { BaseType: var type } || type == NativeSymbols.Void)
			return RejectAtomic(node, Error(node, $"Cannot access values atomically through '{resolved.Type.Name}'",
				null, operand));
		
		if (!_typePool.IsAtomic(type))
			return RejectAtomic(node, Error(node, $"Cannot access '{type.Name}' values atomically", null, node.Place));
		
		var access = node.Op?.Type switch
		{
			null => AtomicAccess.Load,
			TokenType.OpEqual => AtomicAccess.Store,
			TokenType.OpEqualEqual => AtomicAccess.CompareSwap,
			_ => AtomicAccess.Modify
		};
		
		if (FindOrderingError(access, node.Ordering) is { } orderingError)
			return RejectAtomic(node, Error(node, orderingError, null, node.OrderingLocation!.Value));
		
		switch (access)
		{
			case AtomicAccess.Load:
				return new ResolvedAtomicExpressionNode(access, node.Ordering, pointer, null, null, null, type, node);
			
			case AtomicAccess.Store:
				var stored = VisitNode(node.Value!, type);
				return new ResolvedAtomicExpressionNode(access, node.Ordering, pointer, null, null, stored, type, node);
			
			case AtomicAccess.CompareSwap:
				var expected = VisitNode(node.Expected!, type);
				var desired = VisitNode(node.Value!, type);
				return new ResolvedAtomicExpressionNode(access, node.Ordering, pointer, null, expected, desired,
					NativeSymbols.Bool, node);
		}
		
		var op = node.Op!.Value;
		if (FindAtomicOperation(op.Type) is not { } operation)
			return RejectAtomic(node, Error(node, $"Cannot use '{op.Text}' atomically", null, op.SourceLocation));
		
		var owner = type is TypeParameterSymbol ? FindGenericCaller(CurrentResolutionContext) : null;
		var amountType = owner is null ? _typePool.FindAtomicAmountType(operation, type) : type;
		if (amountType is null)
			return RejectAtomic(node, Error(node, $"Cannot use '{op.Text}' atomically on '{type.Name}'", null,
				op.SourceLocation));
		
		if (owner is not null)
			AddAtomicRequirement(owner, new((TypeParameterSymbol)type, operation, op.Text, op.SourceLocation));
		
		var amount = VisitNode(node.Value!, amountType);
		return new ResolvedAtomicExpressionNode(access, node.Ordering, pointer, operation, null, amount, type, node);
	}
	
	private ResolvedInvalidExpressionNode RejectAtomic(AtomicExpressionNode node, ResolvedInvalidExpressionNode error)
	{
		if (node.Expected is { } expected)
			VisitNode(expected, null);
		
		if (node.Value is { } value)
			VisitNode(value, null);
		
		return error;
	}
	
	private static string? FindOrderingError(AtomicAccess access, AtomicOrdering ordering) => (access, ordering) switch
	{
		(AtomicAccess.Load, AtomicOrdering.Release or AtomicOrdering.AcquireRelease) =>
			$"Cannot use '{DescribeOrdering(ordering)}' on atomic loads",
		(AtomicAccess.Store, AtomicOrdering.Acquire or AtomicOrdering.AcquireRelease) =>
			$"Cannot use '{DescribeOrdering(ordering)}' on atomic stores",
		_ => null
	};
	
	private static string DescribeOrdering(AtomicOrdering ordering) => ordering switch
	{
		AtomicOrdering.Relaxed => "relaxed",
		AtomicOrdering.Acquire => "acquire",
		AtomicOrdering.Release => "release",
		_ => "acquire release"
	};
	
	private static BinaryOperation? FindAtomicOperation(TokenType op) => op switch
	{
		TokenType.OpPlusEqual => BinaryOperation.Addition,
		TokenType.OpMinusEqual => BinaryOperation.Subtraction,
		TokenType.OpPlusPercentEqual => BinaryOperation.WrappingAddition,
		TokenType.OpMinusPercentEqual => BinaryOperation.WrappingSubtraction,
		TokenType.OpAmpersandEqual => BinaryOperation.BitwiseAnd,
		TokenType.OpBarEqual => BinaryOperation.BitwiseOr,
		TokenType.OpHatEqual => BinaryOperation.BitwiseXor,
		_ => null
	};
	
	public IResolvedExpressionNode Visit(SpreadExpressionNode node) =>
		Error(node, "Cannot spread arguments into calls without variadic parameters", CurrentTargetType);
	
	public IResolvedExpressionNode Visit(BorrowExpressionNode node)
	{
		var hint = node.IsMutable ? null : ExpectedType is BorrowType borrow ? borrow.Target : ExpectedType;
		var place = hint is null ? VisitNode(node.Value, null) : VisitHinted(node.Value, hint);
		if (!node.IsMutable && survey is not null && IsOpenValue(place))
			return place;
		
		if (!IsInvalid(place) && place.Type is UntypedType)
			place = MaterializeAsDefault(place);
		
		if (IsInvalid(place))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		place = Decay(place);
		if (node.IsMutable)
			place = MakeWritable(place);
		
		return new ResolvedBorrowExpressionNode(place, _typePool.GetBorrowType(place.Type, node.IsMutable), false,
			node);
	}
	
	private bool ReportArgumentModes(IReadOnlyList<ICallable> candidates, IReadOnlyList<IResolvedExpressionNode> args,
		TypeSymbol? target)
	{
		if (ReportMovedFunctionParameters(candidates, args))
			return true;
		
		var relaxed = ResolveCallable(candidates, args, MaterializationMode.Overload, target, true);
		if (relaxed.Count != 1)
			return false;
		
		var diagnostics = args
			.Select((arg, i) => ReportArgumentMode(relaxed[0].Callable, i, arg))
			.OfType<Diagnostic>()
			.ToList();
		
		Diagnostics.AddRange(diagnostics);
		return diagnostics.Count > 0;
	}
	
	private bool ReportMovedFunctionParameters(IReadOnlyList<ICallable> candidates,
		IReadOnlyList<IResolvedExpressionNode> args)
	{
		if (candidates is not [var callable])
			return false;
		
		var diagnostics = args
			.Where((arg, i) => i < callable.ParameterTypes.Length &&
			                   IsMovedFunctionParameter(arg, callable.ParameterTypes[i]))
			.Select(static arg => new Diagnostic(DiagnosticSeverity.Error, arg.Syntax.SourceLocation,
				"Cannot move read-only parameters"))
			.ToList();
		
		Diagnostics.AddRange(diagnostics);
		return diagnostics.Count > 0;
	}
	
	private static Diagnostic ReportImplicitBorrow(IResolvedExpressionNode arg)
	{
		var (source, range) = arg.Syntax.SourceLocation;
		return new(DiagnosticSeverity.Error, arg.Syntax.SourceLocation, "Cannot mutably borrow arguments implicitly")
		{
			Fixes = [DiagnosticFix.Replace("Add 'mut'", new(source, new(range.Start, range.Start)), "mut ")]
		};
	}
	
	private Diagnostic? ReportArgumentMode(ICallable callable, int index, IResolvedExpressionNode arg)
	{
		var argument = arg as ResolvedMutArgumentExpressionNode;
		var hasParameter = index < callable.ParameterTypes.Length;
		var parameterType = hasParameter ? callable.ParameterTypes[index] : null;
		var isMut = hasParameter && (callable.GetMode(index) == ParameterMode.Mut ||
		                             parameterType is BorrowType { IsMutable: true });
		
		var name = hasParameter ? callable.GetParameterName(index) : null;
		
		if (arg.Syntax is BorrowExpressionNode { IsMutable: false } immArgument && arg.Type is BorrowType &&
		    parameterType is not BorrowType && (!hasParameter || callable.GetMode(index) != ParameterMode.Imm))
			return new(DiagnosticSeverity.Error, immArgument.Keyword.SourceLocation, name is null
				? "Only an argument to an 'imm' parameter can be marked 'imm'"
				: $"'{name}' isn't an 'imm' parameter")
			{
				Fixes = [DiagnosticFix.Remove("Remove 'imm'", immArgument.Keyword.SourceLocation)]
			};
		
		if (isMut && arg.Syntax is OwnExpressionNode ownArgument)
			return new(DiagnosticSeverity.Error, ownArgument.Keyword.SourceLocation, name is null
				? "Cannot move arguments into 'mut' parameters"
				: $"'{name}' isn't an 'own' parameter");
		
		if (argument is null)
		{
			if (isMut && !(arg.Type is BorrowType && parameterType is BorrowType))
				return arg is ResolvedAssignmentExpressionNode || TypeChecker.IsLValue(arg)
					? ReportImplicitBorrow(arg)
					: new(DiagnosticSeverity.Error, arg.Syntax.SourceLocation,
						$"Cannot mutably borrow {TypeChecker.DescribeUnstored(arg)}");
			
			return !isMut && parameterType is not null && NeedsLending(arg, parameterType, callable.GetMode(index))
				? ReportImplicitBorrow(arg)
				: null;
		}
		
		var keyword = ((BorrowExpressionNode)argument.Syntax).Keyword;
		if (!isMut)
			return new(DiagnosticSeverity.Error, keyword.SourceLocation, name is null
				? "Only an argument to a 'mut' parameter can be marked 'mut'"
				: $"'{name}' isn't a 'mut' parameter")
			{
				Fixes = [DiagnosticFix.Remove("Remove 'mut'", keyword.SourceLocation)]
			};
		
		if (GetMutTarget(parameterType, callable.GetMode(index)) is not { } declared ||
		    IsMutPlaceOf(argument.Place, declared))
			return null;
		
		return new(DiagnosticSeverity.Error, argument.Place.Syntax.SourceLocation,
			$"Cannot mutably borrow '{argument.Place.Type.Name}' values as '{declared.Name}'");
	}
	
	private static TypeSymbol? GetMutTarget(TypeSymbol? parameterType, ParameterMode mode) => parameterType switch
	{
		PointerType { BaseType: var declared } when mode == ParameterMode.Mut => declared,
		BorrowType { IsMutable: true, Target: var target } => target,
		_ => null
	};
	
	private bool IsMutPlaceOf(IResolvedExpressionNode place, TypeSymbol declared) =>
		place.Type == declared || Decay(place).Type == declared ||
		declared is DynType dyn && CanErase(Decay(place).Type, dyn);
	
	private static TypeSymbol GetArgumentType(IResolvedExpressionNode arg) =>
		arg is ResolvedMutArgumentExpressionNode argument ? argument.Place.Type : arg.Type;
	
	private static string DescribeWrittenType(IResolvedExpressionNode value) => value switch
	{
		ResolvedLiteralExpressionNode { UntypedSource: { } source } => source.Name,
		_ => DiagnosticReporter.DescribeType(GetArgumentType(value))
	};
}