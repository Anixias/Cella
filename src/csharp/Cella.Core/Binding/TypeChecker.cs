using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class TypeChecker(ConstantEvaluator evaluator, TypePool typePool, ModuleIndex modules)
	: IResolvedStatementNodeVisitor, IResolvedDeclarationNodeVisitor, IResolvedExpressionNodeVisitor
{
	public DiagnosticList Diagnostics { get; } = new();
	
	private readonly Stack<(TypeSymbol? Type, FunctionSymbol Function)> _returns = [];
	private int continueDepth;
	private int breakDepth;
	private int overflows;
	private FileSymbol currentFile = null!;
	private TypeSymbol? currentType;
	
	public void Check(ResolvedFileNode root) => VisitNode(root);
	
	private void VisitNode(IResolvedStatementNode node) => ((IResolvedStatementNodeVisitor)this).Visit(node);
	private void VisitNode(IResolvedDeclarationNode node) => ((IResolvedDeclarationNodeVisitor)this).Visit(node);
	private void VisitNode(IResolvedExpressionNode node) => ((IResolvedExpressionNodeVisitor)this).Visit(node);
	
	public void Visit(ResolvedFileNode node)
	{
		currentFile = node.Symbol;
		foreach (var declaration in node.Declarations)
			VisitNode(declaration);
	}
	
	public void Visit(ResolvedFunctionNode node)
	{
		if (node.Body is not IResolvedStatementNode statement)
			return;
		
		_returns.Push((node.FunctionInfo.Signature.ReturnType, node.FunctionInfo.Symbol));
		VisitNode(statement);
		_returns.Pop();
	}
	
	public void Visit(ResolvedRecordNode node)
	{
		currentType = node.Symbol;
		foreach (var member in node.Members)
			VisitNode(member);
		
		currentType = null;
	}
	
	public void Visit(ResolvedFieldNode node)
	{
	}
	
	public void Visit(ResolvedTraitNode node)
	{
		currentType = node.Symbol.Self;
		foreach (var member in node.Members)
			VisitNode(member);
		
		currentType = null;
	}
	
	public void Visit(ResolvedImplNode node)
	{
		currentType = node.Target;
		foreach (var member in node.Members)
			VisitNode(member);
		
		currentType = null;
	}
	
	public void Visit(ResolvedEnumNode node)
	{
		currentType = node.Symbol;
		foreach (var member in node.Members)
			VisitNode(member);
		
		currentType = null;
	}
	
	public void Visit(ResolvedExternalFunctionNode node)
	{
	}
	
	public void Visit(ResolvedGlobalNode node)
	{
		if (node.Info.Initializer is not { } initializer)
			return;
		
		if (initializer is ResolvedUndefExpressionNode)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, initializer.Syntax.SourceLocation,
				$"Cannot use 'undef' on {DescribeGlobals(node.Info.Symbol)}"));
			
			return;
		}
		
		var errorCount = Diagnostics.ErrorCount;
		VisitNode(initializer);
		
		if (node.Info.Value is not null || Diagnostics.ErrorCount != errorCount ||
		    FindNonConstant(initializer) is not { } blocking)
			return;
		
		if (typePool.HoldsBorrows(node.Info.Type))
			Diagnostics.Add(new(DiagnosticSeverity.Error,
				node.Syntax is GlobalNode global ? global.Type.SourceLocation : blocking.Syntax.SourceLocation,
				$"Cannot store borrows in {(node.Info.Symbol.ContainingType is null ? "module" : "static")} values"));
		else
			Diagnostics.Add(new(DiagnosticSeverity.Error, blocking.Syntax.SourceLocation,
				$"The initial value of '{node.Info.Symbol.Name}' must be a constant"));
	}
	
	public void Visit(ResolvedBlockStatementNode node)
	{
		foreach (var statement in node.Statements)
			VisitNode(statement);
	}
	
	public void Visit(ResolvedBreakExpressionNode node)
	{
		if (breakDepth <= 0)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				"Cannot use 'break' outside loops"));
	}
	
	public void Visit(ResolvedContinueExpressionNode node)
	{
		if (continueDepth <= 0)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				"Cannot use 'cont' outside loops"));
	}
	
	public void Visit(ResolvedExpressionStatementNode node)
	{
		if (!IsAllowedAsStatement(node.Expression))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				"Cannot use values as statements"));
		
		// ^Need to check for purity of expressions. Pure expressions as statements is either an error or a warning
		VisitNode(node.Expression);
	}
	
	public void Visit(ResolvedDropStatementNode node)
	{
		var target = node.Target;
		if (target is ResolvedInvalidExpressionNode)
			return;
		
		if (!IsThroughPointer(target))
			Diagnostics.Add(new(DiagnosticSeverity.Error, target.Syntax.SourceLocation,
				"Cannot use 'drop' except through pointers"));
		else if (target.Type is not DynType && !typePool.NeedsDrop(target.Type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, target.Syntax.SourceLocation,
				$"'{target.Type.Name}' has no destructor"));
		
		VisitNode(target);
	}
	
	public void Visit(ResolvedIfStatementNode node)
	{
		VisitNode(node.Condition);
		VisitNode(node.Then);
		
		if (node.Else is { } @else)
			VisitNode(@else);
	}
	
	public void Visit(ResolvedInvalidStatementNode node) =>
		throw new InvalidOperationException();
	
	public void Visit(ResolvedMatchStatementNode node)
	{
		if (node.IsMut)
			CheckMutPlace(node.Value);
		
		VisitNode(node.Value);
		foreach (var arm in node.Arms)
		{
			VisitPatterns(arm.Patterns, arm.Guard);
			VisitNode(arm.Body);
		}
	}
	
	private void VisitPatterns(IEnumerable<ResolvedPattern> patterns, IResolvedExpressionNode? guard)
	{
		foreach (var pattern in patterns)
			VisitPattern(pattern);
		
		if (guard is not null)
			VisitNode(guard);
	}
	
	private void VisitPattern(ResolvedPattern pattern)
	{
		if (pattern.Test is { } test)
			VisitNode(test);
		
		foreach (var element in pattern.Elements.OfType<ResolvedPattern>())
			VisitPattern(element);
	}
	
	public void Visit(ResolvedMatchExpressionNode node)
	{
		if (node.IsMut)
			CheckMutPlace(node.Value);
		
		VisitNode(node.Value);
		foreach (var arm in node.Arms)
		{
			VisitPatterns(arm.Patterns, arm.Guard);
			VisitNode(arm.Value);
			CheckConsumed(arm.Value);
		}
	}
	
	public void Visit(ResolvedReturnExpressionNode node)
	{
		var (expected, function) = _returns.Peek();
		var actual = node.Value?.Type ?? NativeSymbols.Void;
		
		if (actual is not NeverType && !AreTypesCompatible(expected, actual))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation, node.Value is null
				? $"{DiagnosticReporter.DescribeFunction(function, true)} must return a value"
				: $"Cannot return value of type '{actual.Name}': Expected type '{expected?.Name ?? "void"}'"));
		
		if (node.Value is not { } value)
			return;
		
		VisitNode(value);
		CheckConsumed(value);
		CheckBorrowedTemporary(value);
		ReportRedundantOwn(value);
	}
	
	private void ReportRedundantOwn(IResolvedExpressionNode value)
	{
		if (value is ResolvedMatchExpressionNode match)
		{
			foreach (var arm in match.Arms)
				ReportRedundantOwn(arm.Value);
		}
		else if (value is ResolvedOwnExpressionNode { Syntax: OwnExpressionNode syntax } owned &&
		         IsOwnedLocally(owned.Value))
			Diagnostics.Add(new(DiagnosticSeverity.Hint, syntax.Keyword.SourceLocation, "Redundant 'own'"));
	}
	
	private static bool IsOwnedLocally(IResolvedExpressionNode place) => place switch
	{
		ResolvedAccessExpressionNode { Member: FieldSymbol } node => IsOwnedLocally(node.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } node => IsOwnedLocally(node.Target),
		ResolvedVarExpressionNode { Symbol: LocalVariableSymbol { IsBorrowBinding: false } } or
			ResolvedVarExpressionNode { Symbol: ParameterSymbol { Mode: ParameterMode.Own } } => true,
		_ => false
	};
	
	public void Visit(ResolvedVarStatementNode node)
	{
		if (node.Initializer is { } initializer)
		{
			// Special case: If undef, don't check (it will throw an error)
			if (initializer is not ResolvedUndefExpressionNode)
			{
				VisitNode(initializer);
				CheckConsumed(initializer);
				CheckBorrowedTemporary(initializer);
			}
		}
	}
	
	public void Visit(ResolvedWhileStatementNode node)
	{
		VisitNode(node.Condition);
		VisitLoopBody(node.Body);
	}
	
	public void Visit(ResolvedDoWhileStatementNode node)
	{
		VisitNode(node.Condition);
		VisitLoopBody(node.Body);
	}
	
	public void Visit(ResolvedLoopStatementNode node) => VisitLoopBody(node.Body);
	
	public void Visit(ResolvedRangeForStatementNode node)
	{
		VisitNode(node.Start);
		VisitNode(node.End);
		if (node.Source is { } source)
			VisitNode(source.Value);
		
		if (node.Element is { } element)
			VisitNode(element.Value);
		
		VisitLoopBody(node.Body);
	}
	
	public void Visit(ResolvedCursorForStatementNode node)
	{
		VisitNode(node.Cursor.Value);
		CheckConsumed(node.Cursor.Value);
		VisitNode(node.Step);
		if (node.Element is { } element)
			VisitNode(element.Value);
		
		VisitLoopBody(node.Body);
	}
	
	private void VisitLoopBody(IResolvedStatementNode body)
	{
		continueDepth++;
		breakDepth++;
		VisitNode(body);
		breakDepth--;
		continueDepth--;
	}
	
	private bool AreTypesCompatible(TypeSymbol? expected, TypeSymbol? actual) => expected == actual;
	
	internal static bool IsLValue(IResolvedExpressionNode expression) => expression switch
	{
		ResolvedVarExpressionNode => true,
		ResolvedGlobalExpressionNode => true,
		ResolvedAccessExpressionNode { Member: FieldSymbol } e => IsLValue(e.Target),
		ResolvedIndexerExpressionNode { Target.Type: StringType } => true,
		ResolvedIndexerExpressionNode e => IsLValue(e.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
		ResolvedAssignmentExpressionNode e => IsLValue(e.Left),
		_ => false
	};
	
	private bool IsAllowedAsStatement(IResolvedExpressionNode expression) => expression switch
	{
		ResolvedFunctionCallExpressionNode call => !IsGetterCall(call),
		ResolvedIndirectCallExpressionNode => true,
		ResolvedConstructorCallExpressionNode => true,
		ResolvedAssignmentExpressionNode => true,
		ResolvedPropertyAssignmentExpressionNode => true,
		ResolvedReturnExpressionNode or ResolvedBreakExpressionNode or ResolvedContinueExpressionNode => true,
		ResolvedAtomicExpressionNode => true,
		ResolvedErasedCallExpressionNode => true,
		_ => false
	};
	
	private static bool IsGetterCall(IResolvedExpressionNode node) => node is ResolvedFunctionCallExpressionNode
	{
		Function.Symbol: { Property.Getter: FunctionAccessor { Function: var getter } } function
	} && getter == function;
	
	private void ReportZeroDivisor(IResolvedExpressionNode divisor)
	{
		if (evaluator.IsZero(divisor))
			Diagnostics.Add(new(DiagnosticSeverity.Error, divisor.Syntax.SourceLocation, "Division by zero"));
	}
	
	private static VariableSymbol? FindImmutableBinding(IResolvedExpressionNode place) => place switch
	{
		ResolvedVarExpressionNode { Symbol: LocalVariableSymbol { IsMutable: false } local } => local,
		ResolvedUnaryOpExpressionNode
		{
			Operation.Op: TokenType.OpStar,
			Operand: ResolvedVarExpressionNode
			{
				Symbol: LocalVariableSymbol { IsBorrowBinding: true, IsMutBinding: false } local
			}
		} => local,
		ResolvedVarExpressionNode
			{
				Symbol: ParameterSymbol { Mode: ParameterMode.ReadOnly or ParameterMode.Imm } parameter
			} =>
			parameter,
		ResolvedGlobalExpressionNode { Symbol: { IsMutable: false } global } => global,
		ResolvedAccessExpressionNode { Member: FieldSymbol } e => FindImmutableBinding(e.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } e => FindImmutableBinding(e.Target),
		_ => null
	};
	
	private static bool IsDeferredWrite(ResolvedAssignmentExpressionNode node) =>
		node is { Left: ResolvedVarExpressionNode { Symbol: LocalVariableSymbol { IsDeferred: true } } };
	
	private static string DescribeImmutableWrite(IResolvedExpressionNode place, VariableSymbol binding) =>
		binding switch
		{
			LocalVariableSymbol { Captured: LocalVariableSymbol { IsDeferred: true } } =>
				"Cannot assign values in closures",
			LocalVariableSymbol { IsDeferred: true } when place is not ResolvedVarExpressionNode =>
				"Cannot assign parts of values",
			_ => $"Cannot reassign {DescribeImmutable(binding)}"
		};
	
	private static string DescribeImmutable(VariableSymbol binding) => binding switch
	{
		LocalVariableSymbol { Captured: { } captured } => DescribeImmutable(captured),
		LocalVariableSymbol { IsLoopBinding: true } => "read-only loop variables",
		LocalVariableSymbol { IsPatternBinding: true } => "read-only pattern bindings",
		ParameterSymbol => "read-only parameters",
		_ => "values"
	};
	
	private IResolvedExpressionNode? FindNonConstant(IResolvedExpressionNode node)
	{
		if (evaluator.Evaluate(node) is not null)
			return null;
		
		if (!CanFold(node))
			return node;
		
		return ConstantEvaluator.GetOperands(node).Select(FindNonConstant).FirstOrDefault(static n => n is not null) ??
		       node;
	}
	
	private static bool CanFold(IResolvedExpressionNode node) => node switch
	{
		ResolvedUnaryOpExpressionNode { Operation: NativeImpl { Op: not (TokenType.OpAt or TokenType.OpStar) } } =>
			true,
		ResolvedUnaryOpExpressionNode => false,
		ResolvedBinaryOpExpressionNode { Operation: NativeImpl } => true,
		ResolvedBinaryOpExpressionNode => false,
		ResolvedChainedExpressionNode n => n.Links.All(static link => link.Function is null),
		_ => true
	};
	
	public void Visit(ResolvedAccessExpressionNode node)
	{
		VisitNode(node.Target);
	}
	
	public void Visit(ResolvedArrayExpressionNode node)
	{
		foreach (var value in node.Values)
		{
			VisitNode(value);
			CheckConsumed(value);
		}
	}
	
	public void Visit(ResolvedArrayBuildExpressionNode node)
	{
		VisitNode(node.Make);
		VisitNode(node.Length);
	}
	
	public void Visit(ResolvedPackExpressionNode node)
	{
		foreach (var value in node.Values)
		{
			VisitNode(value);
			CheckConsumed(value);
		}
	}
	
	public void Visit(ResolvedAtomicExpressionNode node)
	{
		if (node.Access is not (AtomicAccess.Load or AtomicAccess.Fence) && node.Pointer is { } target &&
		    AddressesConstant(target))
			Diagnostics.Add(new(DiagnosticSeverity.Error, target.Syntax.SourceLocation,
				"Cannot write through the address of constants"));
		
		if (node.Pointer is { } pointer)
			VisitNode(pointer);
		
		if (node.Expected is { } expected)
			VisitNode(expected);
		
		if (node.Value is { } value)
			VisitNode(value);
	}
	
	public void Visit(ResolvedInterpolatedStringExpressionNode node)
	{
		foreach (var value in node.Values)
			VisitNode(value);
	}
	
	public void Visit(ResolvedFStrExpressionNode node)
	{
		if (node.Text is { } text)
			VisitNode(text);
		
		foreach (var value in node.Values)
			VisitNode(value);
	}
	
	public void Visit(ResolvedFStrPartExpressionNode node)
	{
		VisitNode(node.Target);
		VisitNode(node.Index);
	}
	
	public void Visit(ResolvedAssignmentExpressionNode node)
	{
		if (node.Left is ResolvedAccessExpressionNode { Member: PropertySymbol { Setter: null } })
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				"Cannot reassign read-only properties"));
		else if (!IsLValue(node.Left))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				$"Cannot assign to {DescribeUnstored(node.Left)}"));
		else if (ReportUnwritable(node.Left) is { } unwritable)
			Diagnostics.Add(unwritable);
		else if (FindReadOnlyOperator(node.Left) is var (owner, name))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				$"'{owner.Name}' has no writable '{name}'"));
		else if (IsThroughReadOnlyBorrow(node.Left))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				"Cannot write through read-only borrows"));
		else if (IsThroughConstantAddress(node.Left))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				"Cannot write through the address of constants"));
		else if (FindIndexedString(node.Left) is { } text)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				$"Cannot write through '{text.Name}'"));
		else if (!IsDeferredWrite(node) && FindImmutableBinding(node.Left) is { } binding)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				DescribeImmutableWrite(node.Left, binding)));
		else if (node.IsOwnStore)
			CheckOwnStore(node);
		
		if (node.Op.Type is TokenType.OpSlashEqual or TokenType.OpPercentEqual)
			ReportZeroDivisor(node.Right);
		
		VisitNode(node.Left);
		VisitNode(node.Right);
		if (node.Operation is not null)
			return;
		
		CheckConsumed(node.Right);
		CheckBorrowedTemporary(node.Right);
	}
	
	public void Visit(ResolvedPropertyExpressionNode node) => throw new InvalidOperationException();
	
	public void Visit(ResolvedPropertyAssignmentExpressionNode node)
	{
		if (node.Receiver is { } receiver)
		{
			if (node.Getter.Signature.GetMode(0) == ParameterMode.Mut ||
			    node.Setter.Signature.GetMode(0) == ParameterMode.Mut)
				CheckMutPlace(receiver);
			
			VisitNode(receiver);
		}
		
		if (node.Operation is not null && node.Op.Type is TokenType.OpSlashEqual or TokenType.OpPercentEqual)
			ReportZeroDivisor(node.Right);
		
		VisitNode(node.Right);
	}
	
	private void CheckOwnStore(ResolvedAssignmentExpressionNode node)
	{
		if (node.Operation is not null)
			Diagnostics.Add(new(DiagnosticSeverity.Error,
				((OwnExpressionNode)((BinaryOpExpressionNode)node.Syntax).Left).Keyword.SourceLocation,
				"Cannot use 'own' on compound assignments"));
		else if (!IsThroughPointer(node.Left))
			Diagnostics.Add(new(DiagnosticSeverity.Error, ((BinaryOpExpressionNode)node.Syntax).Left.SourceLocation,
				"Cannot assign with 'own' except through pointers"));
		else if (!typePool.NeedsDrop(node.Left.Type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				$"Cannot move '{node.Left.Type.Name}' values into a pointer"));
	}
	
	public void Visit(ResolvedBinaryOpExpressionNode node)
	{
		if (node.Operation is NativeImpl { Op: TokenType.OpSlash or TokenType.OpPercent })
			ReportZeroDivisor(node.Right);
		
		if (node.Operation is PointerDifferenceImpl { PointerType.BaseType: var element } &&
		    element != NativeSymbols.Void && element != NativeSymbols.Invalid && evaluator.HasNoSize(element))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				$"Cannot subtract pointers to zero-size type '{element.Name}'"));
		
		var reported = overflows;
		VisitNode(node.Left);
		VisitNode(node.Right);
		if (overflows > reported)
			return;
		
		if (evaluator.FindOverflowingOperation(node) is { } operation)
			ReportOverflow(node, operation);
	}
	
	private void ReportOverflow(IResolvedExpressionNode node, string operation)
	{
		overflows++;
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
			$"{operation} overflows '{node.Type.Name}'"));
	}
	
	public void Visit(ResolvedChainedExpressionNode node)
	{
		foreach (var operand in node.Operands)
			VisitNode(operand);
		
		for (var i = 0; i < node.Links.Length; i++)
		{
			if (node.Links[i].Function is not { } function)
				continue;
			
			for (var j = 0; j < 2; j++)
			{
				if (function.Signature.GetMode(j) == ParameterMode.Own)
					CheckConsumed(node.Operands[i + j]);
			}
		}
	}
	
	public void Visit(ResolvedConstructorCallExpressionNode node)
	{
		var args = node.Arguments;
		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			VisitNode(arg);
			if (node.Function.Signature.GetMode(i + 1) == ParameterMode.Own)
				CheckConsumed(arg);
		}
	}
	
	public void Visit(ResolvedConversionExpressionNode node)
	{
		if (node.Conversion is EnumConversion { To: EnumSymbol { IsExternal: false } enumType } &&
		    evaluator.Evaluate(node.Source) is IntegerConstant { Value: var value } && evaluator.Evaluate(node) is null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Source.Syntax.SourceLocation,
				$"'{enumType.Name}' has no case with the value {value}"));
		
		var reported = overflows;
		VisitNode(node.Source);
		if (overflows > reported || evaluator.FindLostConstant(node) is not var (lost, target))
			return;
		
		overflows++;
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
			$"'{lost}' doesn't fit in '{target.Name}'"));
	}
	
	private static string DescribeMatchValue(Constant constant) => constant switch
	{
		IntegerConstant integer => integer.Value.ToString(),
		BoolConstant flag => flag.Value ? "true" : "false",
		ZeroConstant { Type: IntegerType } => "0",
		ZeroConstant { Type: var type } when type == NativeSymbols.Bool => "false",
		_ => "null"
	};
	
	public void Visit(ResolvedFunctionCallExpressionNode node)
	{
		var args = node.Arguments;
		var signature = node.Function.Signature;
		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			VisitNode(arg);
			if (i >= signature.ParameterTypes.Length)
				continue;
			
			CheckImmArgument(node.Syntax, arg, signature.GetMode(i),
				i == 0 && node.Function.Symbol.Kind == FunctionKind.Method);
			
			if (signature.GetMode(i) == ParameterMode.Own)
				CheckConsumed(arg);
		}
	}
	
	private static bool NamesPlace(IExpressionNode syntax) => syntax is VarExpressionNode or AccessExpressionNode
		or IndexerExpressionNode or UnaryOpExpressionNode { Op.Type: TokenType.OpStar };
	
	private void CheckImmArgument(IExpressionNode call, IResolvedExpressionNode arg, ParameterMode mode,
		bool isReceiver)
	{
		if (mode == ParameterMode.Imm && !isReceiver && call is CallExpressionNode { Arguments: var written } &&
		    written.Contains(arg.Syntax) && NamesPlace(arg.Syntax) && IsLValue(arg))
			Diagnostics.Add(new(DiagnosticSeverity.Error, arg.Syntax.SourceLocation,
				"Cannot borrow arguments implicitly"));
	}
	
	public void Visit(ResolvedFunctionReferenceExpressionNode node)
	{
	}
	
	public void Visit(ResolvedClosureExpressionNode node)
	{
	}
	
	public void Visit(ResolvedOwnExpressionNode node)
	{
		var location = node.Value.Syntax.SourceLocation;
		var place = SkipAssignment(node.Value);
		if (!IsLValue(place))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot move {DescribeUnstored(place)}"));
		else if (FindGlobal(place) is { } global)
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot move {DescribeGlobals(global)}"));
		else if (IsThroughBorrow(place))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, "Cannot move borrowed values"));
		else if (IsThroughPointer(place) && typePool.IsCopy(node.Value.Type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"Cannot move '{node.Value.Type.Name}' values out of a pointer"));
		else if (!typePool.IsCopy(node.Value.Type) && ReportUnwritable(place) is { } unwritable)
			Diagnostics.Add(unwritable);
		
		VisitNode(node.Value);
	}
	
	private void CheckConsumed(IResolvedExpressionNode value)
	{
		var place = SkipAssignment(value);
		if (value is ResolvedOwnExpressionNode || !IsLValue(place) || typePool.IsCopy(value.Type))
			return;
		
		if (FindGlobal(place) is { } global)
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation,
				$"Cannot move {DescribeGlobals(global)}"));
		else if (IsThroughBorrow(place))
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation, "Cannot move borrowed values"));
		else if (IsThroughPointer(place))
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation,
				$"Cannot move '{value.Type.Name}' values implicitly"));
		else if (ReportUnwritable(place) is { } unwritable)
			Diagnostics.Add(unwritable);
	}
	
	private Diagnostic? ReportUnwritable(IResolvedExpressionNode place) => place switch
	{
		ResolvedAccessExpressionNode { Member: FieldSymbol field } node =>
			ReportUnwritable(node.Target.Type, field, GetMemberLocation(node.Syntax)) ?? ReportUnwritable(node.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } node => ReportUnwritable(node.Target),
		ResolvedGlobalExpressionNode { Symbol: { ContainingType: { } owner } global } node =>
			modules.IsAccessible(owner, global.WriteVisibility, currentFile, currentType)
				? null
				: DiagnosticReporter.ReportReadOnly(GetMemberLocation(node.Syntax), global.Name,
					global.WriteVisibility),
		_ => null
	};
	
	private Diagnostic? ReportUnwritable(TypeSymbol owner, FieldSymbol field, SourceLocation location)
	{
		if (!modules.IsAccessible(owner, field.Visibility, currentFile, currentType))
			return DiagnosticReporter.ReportHidden(location, field.Name, field.Visibility, true);
		
		return modules.IsAccessible(owner, field.WriteVisibility, currentFile, currentType)
			? null
			: DiagnosticReporter.ReportReadOnly(location, field.Name, field.WriteVisibility);
	}
	
	private static SourceLocation GetMemberLocation(IExpressionNode syntax) =>
		syntax is AccessExpressionNode access ? access.Member.SourceLocation : syntax.SourceLocation;
	
	internal static string DescribeUnstored(IResolvedExpressionNode place) =>
		IsGetterCall(place) ? "properties" : "unstored values";
	
	private static GlobalSymbol? FindGlobal(IResolvedExpressionNode place) => place switch
	{
		ResolvedGlobalExpressionNode node => node.Symbol,
		ResolvedAccessExpressionNode { Member: FieldSymbol } node => FindGlobal(node.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } node => FindGlobal(node.Target),
		_ => null
	};
	
	private static string DescribeGlobals(GlobalSymbol global) =>
		$"{(global.ContainingType is null ? "module" : "static")} {(global.IsMutable ? "variables" : "values")}";
	
	private void CheckBorrowedTemporary(IResolvedExpressionNode value)
	{
		if (value is ResolvedBorrowExpressionNode { IsImplicit: true } borrow &&
		    !IsLValue(SkipAssignment(borrow.Place)))
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation,
				$"Cannot borrow {DescribeUnstored(borrow.Place)}"));
	}
	
	private static IResolvedExpressionNode SkipAssignment(IResolvedExpressionNode value) =>
		value is ResolvedAssignmentExpressionNode assignment ? assignment.Left : value;
	
	private static bool IsThroughPointer(IResolvedExpressionNode place) => place switch
	{
		ResolvedAccessExpressionNode n => IsThroughPointer(n.Target),
		ResolvedIndexerExpressionNode n => IsThroughPointer(n.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } n => n.Operand is not ResolvedVarExpressionNode
		{
			Symbol: ParameterSymbol { Mode: ParameterMode.Mut } or LocalVariableSymbol { IsBorrowBinding: true }
		} && n.Operand.Type is not BorrowType,
		_ => false
	};
	
	private static bool IsThroughBorrow(IResolvedExpressionNode place) => place switch
	{
		ResolvedAccessExpressionNode n => IsThroughBorrow(n.Target),
		ResolvedIndexerExpressionNode n => IsThroughBorrow(n.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } n => n.Operand.Type is BorrowType,
		_ => false
	};
	
	private static TypeSymbol? FindIndexedString(IResolvedExpressionNode place) =>
		place is ResolvedIndexerExpressionNode { Target.Type: StringType type } ? type : null;
	
	private bool IsThroughConstantAddress(IResolvedExpressionNode place) => place switch
	{
		ResolvedAccessExpressionNode { Member: FieldSymbol } n => IsThroughConstantAddress(n.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } n => IsThroughConstantAddress(n.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } n => AddressesConstant(n.Operand),
		_ => false
	};
	
	private bool AddressesConstant(IResolvedExpressionNode pointer) =>
		pointer is ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpAt } address &&
		IsConstantPlace(address.Operand);
	
	private bool IsConstantPlace(IResolvedExpressionNode place) => place switch
	{
		ResolvedVarExpressionNode { Symbol: LocalVariableSymbol { IsMutable: false, IsBorrowBinding: false } } n =>
			!typePool.NeedsDrop(n.Type),
		ResolvedGlobalExpressionNode { Symbol: { IsMutable: false, IsExternal: false } } n =>
			!typePool.NeedsDrop(n.Type),
		ResolvedAccessExpressionNode { Member: FieldSymbol } n => IsConstantPlace(n.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } n => IsConstantPlace(n.Target),
		_ => false
	};
	
	private static bool IsThroughReadOnlyBorrow(IResolvedExpressionNode place) => place switch
	{
		ResolvedAccessExpressionNode { Member: FieldSymbol } n => IsThroughReadOnlyBorrow(n.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } n => IsThroughReadOnlyBorrow(n.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar, Operand.Type: BorrowType borrow } =>
			!borrow.IsMutable,
		_ => false
	};
	
	public void Visit(ResolvedMutArgumentExpressionNode node)
	{
		if (FindMutPlaceError(node.Place) is { } error)
			Diagnostics.Add(node.OnlyMutOperator is { } name
				? new(DiagnosticSeverity.Error, error.SourceLocation,
					$"'{node.Place.Type.Name}' has no read-only '{name}'")
				: error);
		
		VisitNode(node.Place);
	}
	
	public void Visit(ResolvedBorrowExpressionNode node)
	{
		if (node.IsMutable)
			CheckMutPlace(node.Place);
		else if (!node.IsImplicit || IsLValue(node.Place))
			CheckBorrowedPlace(node.Place);
		
		VisitNode(node.Place);
	}
	
	private void CheckBorrowedPlace(IResolvedExpressionNode place)
	{
		var location = place.Syntax.SourceLocation;
		if (!IsLValue(SkipAssignment(place)))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot borrow {DescribeUnstored(place)}"));
	}
	
	private void CheckMutPlace(IResolvedExpressionNode place)
	{
		if (FindMutPlaceError(place) is { } error)
			Diagnostics.Add(error);
	}
	
	private Diagnostic? FindMutPlaceError(IResolvedExpressionNode place)
	{
		if (place is not ResolvedAssignmentExpressionNode && IsLValue(place) &&
		    ReportUnwritable(place) is { } unwritable)
			return unwritable;
		
		var message = place switch
		{
			ResolvedAssignmentExpressionNode assignment =>
				IsDeferredWrite(assignment) && FindImmutableBinding(assignment.Left) is { } deferred
					? $"Cannot mutably borrow {DescribeImmutable(deferred)}"
					: null,
			_ when !IsLValue(place) => $"Cannot mutably borrow {DescribeUnstored(place)}",
			_ when FindReadOnlyOperator(place) is var (owner, name) => $"'{owner.Name}' has no writable '{name}'",
			_ when IsThroughReadOnlyBorrow(place) => "Cannot mutably borrow through read-only borrows",
			_ when FindIndexedString(place) is { } text => $"Cannot mutably borrow through '{text.Name}'",
			_ when FindImmutableBinding(place) is { } binding => $"Cannot mutably borrow {DescribeImmutable(binding)}",
			_ => null
		};
		
		return message is null ? null : new Diagnostic(DiagnosticSeverity.Error, place.Syntax.SourceLocation, message);
	}
	
	private static (TypeSymbol Owner, string Name)? FindReadOnlyOperator(IResolvedExpressionNode place) => place switch
	{
		ResolvedAccessExpressionNode { Member: FieldSymbol } n => FindReadOnlyOperator(n.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } n => FindReadOnlyOperator(n.Target),
		ResolvedUnaryOpExpressionNode
		{
			Operation.Op: TokenType.OpStar, Operand: ResolvedFunctionCallExpressionNode
			{
				Type: BorrowType { IsMutable: false },
				Function.Symbol.Syntax: FunctionNode { Identifier.Text: var name and ("*" or "[]") },
				Arguments: [var receiver, ..]
			}
		} => (receiver.Type is BorrowType borrow ? borrow.Target : receiver.Type, name),
		_ => null
	};
	
	public void Visit(ResolvedFunctionGroupExpressionNode node)
	{
	}
	
	public void Visit(ResolvedCaseNameExpressionNode node)
	{
	}
	
	public void Visit(ResolvedEnumCaseExpressionNode node)
	{
		if (node.Type is EnumSymbol { IsMatch: true } enumType && node.Payload is [var payload] &&
		    evaluator.Evaluate(payload) is { } constant and not InvalidConstant)
		{
			var holder = ConstantEvaluator.GetMatchValue(constant) is { } value
				? typePool.FindCase(enumType, value)
				: TypePool.GetElseCase(enumType);
			
			if (holder != node.Case)
				Diagnostics.Add(new(DiagnosticSeverity.Error, payload.Syntax.SourceLocation,
					$"'{enumType.Name}.{node.Case.Name}' can't hold {DescribeMatchValue(constant)}"));
		}
		
		foreach (var value in node.Payload)
		{
			VisitNode(value);
			CheckConsumed(value);
		}
	}
	
	public void Visit(ResolvedIsExpressionNode node)
	{
		if (node.IsMut)
			CheckMutPlace(node.Value);
		
		VisitNode(node.Value);
		VisitPattern(node.Pattern);
	}
	
	public void Visit(ResolvedIndirectCallExpressionNode node)
	{
		VisitNode(node.Target);
		for (var i = 0; i < node.Arguments.Length; i++)
		{
			VisitNode(node.Arguments[i]);
			if (i >= node.FunctionType.ParameterModes.Length)
				continue;
			
			CheckImmArgument(node.Syntax, node.Arguments[i], node.FunctionType.ParameterModes[i], false);
			if (node.FunctionType.ParameterModes[i] == ParameterMode.Own)
				CheckConsumed(node.Arguments[i]);
		}
	}
	
	public void Visit(ResolvedIndexerExpressionNode node)
	{
		if (FindIndexLimit(node.Target) is { } limit &&
		    evaluator.Evaluate(node.Index) is IntegerConstant { Value: var index } && index >= limit)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Index.Syntax.SourceLocation,
				$"Index {index} is out of range for '{node.Target.Type.Name}'"));
		
		VisitNode(node.Target);
		VisitNode(node.Index);
	}
	
	private BigInteger? FindIndexLimit(IResolvedExpressionNode target) => target.Type switch
	{
		ArrayType { Length.Sign: >= 0 } array => array.Length,
		StringType when evaluator.Evaluate(target) is StringConstant { Value: var text } &&
		                ConstantEvaluator.GetBytes(text) is { } bytes => bytes.Length,
		_ => null
	};
	
	public void Visit(ResolvedInvalidExpressionNode node) =>
		throw new InvalidOperationException();
	
	public void Visit(ResolvedLiteralExpressionNode node)
	{
	}
	
	public void Visit(ResolvedSizeOfExpressionNode node)
	{
	}
	
	public void Visit(ResolvedValueParameterExpressionNode node)
	{
	}
	
	public void Visit(ResolvedAlignOfExpressionNode node)
	{
	}
	
	public void Visit(ResolvedNewExpressionNode node)
	{
	}
	
	public void Visit(ResolvedErasedCallExpressionNode node) => VisitNode(node.Call);
	
	public void Visit(ResolvedRecordExpressionNode node)
	{
		foreach (var (field, value) in node.Fields)
		{
			if (ReportUnwritable(node.Type, field, value.Syntax.SourceLocation) is { } unwritable)
				Diagnostics.Add(unwritable);
			
			VisitNode(value);
			CheckConsumed(value);
		}
	}
	
	public void Visit(ResolvedUnaryOpExpressionNode node)
	{
		if (node.Operation?.Op == TokenType.OpAt && !IsLValue(SkipAssignment(node.Operand)))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Operand.Syntax.SourceLocation,
				$"Cannot take the address of {DescribeUnstored(node.Operand)}"));
		
		var reported = overflows;
		VisitNode(node.Operand);
		if (overflows == reported && evaluator.OverflowsNegation(node))
			ReportOverflow(node, "Negation");
	}
	
	public void Visit(ResolvedUndefExpressionNode node)
	{
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
			"Cannot use 'undef' outside variable initializers"));
	}
	
	public void Visit(ResolvedVarExpressionNode node)
	{
	}
	
	public void Visit(ResolvedGlobalExpressionNode node)
	{
	}
}