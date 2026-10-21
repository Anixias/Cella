using System.Diagnostics.CodeAnalysis;
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

public sealed partial class Resolver
{
	[return: NotNullIfNotNull(nameof(source))]
	private IResolvedExpressionNode? ApplyImplicitConversion(IResolvedExpressionNode? source, TypeSymbol target)
	{
		if (source is null)
			return null;
		
		if (source.Type == target)
			return source;
		
		if (IsInvalid(source) || IsInvalid(target))
			return new ResolvedConversionExpressionNode(source, new IdentityConversion(target), source.Syntax);
		
		if (target is BorrowType { IsMutable: false } borrow && source.Type == borrow.Target)
			return new ResolvedBorrowExpressionNode(source, borrow, true, source.Syntax);
		
		if (target is FStrType fstr && Decay(source).Type == NativeSymbols.Str)
			return CreateTextTemplate(Decay(source), fstr);
		
		if (IsDynTarget(target) && ConvertToDyn(source, target) is { } dynValue)
			return dynValue;
		
		if (source is ResolvedBorrowExpressionNode { IsMutable: true, IsImplicit: false } borrowed &&
		    FindLendingConversion(borrowed.Place, target) is { } lending)
			return ApplyUserConversion(new ResolvedMutArgumentExpressionNode(borrowed.Place,
				_typePool.GetPointerType(borrowed.Place.Type), source.Syntax), lending);
		
		if (target is not BorrowType && Decay(source) is var decayed && decayed != source)
			return ApplyImplicitConversion(decayed, target);
		
		if (_conversionTable.FindImplicit(source.Type, target) is { } conversion)
			return new ResolvedConversionExpressionNode(source, conversion, source.Syntax);
		
		if (FindUserConversion(source, target) is { } userConversion)
			return ApplyUserConversion(source, userConversion, null, target);
		
		if (FindLendingConversion(source, target) is not null)
			return Retain(source, Error(source.Syntax, "Cannot mutably borrow arguments implicitly", target));
		
		if (IsDynTarget(target) && ConvertToDyn(source, target) is { } erased)
			return erased;
		
		if (source is ResolvedFunctionGroupExpressionNode group)
			return ReportFunctionMismatch(group, target);
		
		if (IsMovedFunctionParameter(source, target))
			return Retain(source, Error(source.Syntax, "Cannot move read-only parameters", target));
		
		if (HoldsDistinctClosures([source.Type, target]))
			return Retain(source, Error(source.Syntax, "Cannot convert between 'own fun' closures", target));
		
		if (TypePool.ContainsInvalid(source.Type) || TypePool.ContainsInvalid(target))
			return new ResolvedInvalidExpressionNode(source.Syntax, target);
		
		if (target is BorrowType { IsMutable: true } mutTarget && source.Type == mutTarget.Target)
			return Retain(source, Error(source.Syntax, "Cannot mutably borrow values implicitly", target));
		
		if (source is ResolvedLiteralExpressionNode { Type: UntypedIntegerType untyped, Value: BigInteger value } &&
		    target is IntegerType { IsSigned: var isSigned } && !(untyped.IsUnsigned && isSigned))
			return Retain(source, Error(source.Syntax, $"'{value}' doesn't fit in '{target.Name}'", target));
		
		if (DescribeLiteralMismatch(source, target) is { } literalMismatch)
			return Retain(source, Error(source.Syntax, literalMismatch, target));
		
		var from = DiagnosticReporter.DescribeType(source.Type);
		return Retain(source, Error(source.Syntax,
			$"Cannot convert type '{from}' to '{DiagnosticReporter.DescribeType(target)}'", target));
	}
	
	private IntegerType? SmallestFittingType(BigInteger value)
	{
		if (value >= int.MinValue && value <= int.MaxValue)
			return NativeSymbols.Int32;
		
		if (value >= long.MinValue && value <= long.MaxValue)
			return NativeSymbols.Int64;
		
		if (value >= Int128.MinValue && value <= Int128.MaxValue)
			return NativeSymbols.Int128;
		
		return null;
	}
	
	private static bool IsMixedSignComparison(TokenType op, TypeSymbol left, TypeSymbol right) =>
		op is TokenType.OpLess or TokenType.OpLessEqual or TokenType.OpGreater or TokenType.OpGreaterEqual
			or TokenType.OpEqualEqual or TokenType.OpBangEqual &&
		left is IntegerType { IsSigned: var leftSigned } && right is IntegerType { IsSigned: var rightSigned } &&
		leftSigned != rightSigned;
	
	private static bool IsShiftOrRotate(TokenType op) => op is TokenType.OpLessLess or TokenType.OpLessLessEqual
		or TokenType.OpGreaterGreater or TokenType.OpGreaterGreaterEqual or TokenType.OpLessLessLess
		or TokenType.OpLessLessLessEqual or TokenType.OpGreaterGreaterGreater or TokenType.OpGreaterGreaterGreaterEqual;
	
	private Diagnostic? FindShiftRangeError(TokenType op, TypeSymbol valueType, IResolvedExpressionNode amount)
	{
		var isShift = op is TokenType.OpLessLess or TokenType.OpLessLessEqual or TokenType.OpGreaterGreater
			or TokenType.OpGreaterGreaterEqual;
		
		if (!isShift || _evaluator.Evaluate(amount) is not IntegerConstant { Value: var value })
			return null;
		
		var bits = CountBits(valueType);
		if (value >= 0 && value < bits)
			return null;
		
		return new Diagnostic(DiagnosticSeverity.Error, amount.Syntax.SourceLocation,
			$"Shift amount {value} is out of range for '{valueType.Name}'")
		{
			Hints = [$"'{valueType.Name}' has {bits} bits"]
		};
	}
	
	private (IntegerType Signed, IntegerType Unsigned)? FindLossyMixedSign(TypeSymbol left, TypeSymbol right)
	{
		if (left is not IntegerType a || right is not IntegerType b || a.IsSigned == b.IsSigned)
			return null;
		
		var (signedType, unsignedType) = a.IsSigned ? (a, b) : (b, a);
		return _conversionTable.FindImplicit(unsignedType, signedType) is null ? (signedType, unsignedType) : null;
	}
	
	private IntegerType? FindMixedSignType(IntegerType signedType, IntegerType unsignedType)
	{
		if (signedType.Kind == PrimitiveTypeKind.IntSize || unsignedType.Kind == PrimitiveTypeKind.UIntSize)
			return null;
		
		var bits = Math.Max(CountBits(signedType), CountBits(unsignedType) + 1);
		return NativeSymbols.PureIntegerTypes.FirstOrDefault(type =>
			type.IsSigned && type.Kind != PrimitiveTypeKind.IntSize && CountBits(type) >= bits);
	}
	
	private TypeSymbol? UnifyTypes(IList<IResolvedExpressionNode> values)
	{
		if (values.Count == 0)
			return NativeSymbols.Never;
		
		// Materialize untyped integer values left to right
		for (var i = 0; i < values.Count - 1; i++)
		{
			values[i] = MaterializeWithPeer(values[i], values[i + 1].Type);
			values[i + 1] = MaterializeWithPeer(values[i + 1], values[i].Type);
		}
		
		// Propagate materialized values back right to left
		for (var i = values.Count - 1; i > 0; i--)
		{
			values[i] = MaterializeWithPeer(values[i], values[i - 1].Type);
			values[i - 1] = MaterializeWithPeer(values[i - 1], values[i].Type);
		}
		
		for (var i = 0; i < values.Count; i++)
			values[i] = MaterializeAsDefault(values[i]);
		
		TypeSymbol? commonType = values[0].Type;
		for (var i = 1; i < values.Count && commonType is not null; i++)
			commonType = FindCommonType(commonType, values[i].Type);
		
		return commonType;
	}
	
	private TypeSymbol? FindCommonType(TypeSymbol a, TypeSymbol b)
	{
		if (a == b || b is NeverType)
			return a;
		
		if (a is NeverType)
			return b;
		
		if (AnyInvalid(a, b))
			return NativeSymbols.Invalid;
		
		if (a is BorrowType { Target: var aTarget } && aTarget == b)
			return b;
		
		if (b is BorrowType { Target: var bTarget } && bTarget == a)
			return a;
		
		if (_conversionTable.FindImplicit(a, b) is not null)
			return b;
		
		if (_conversionTable.FindImplicit(b, a) is not null)
			return a;
		
		return null;
	}
	
	[return: NotNullIfNotNull(nameof(node))]
	private IResolvedExpressionNode? CoerceToType(IResolvedExpressionNode? node, TypeSymbol target)
	{
		if (node?.Type is NeverType)
			return MaterializeExpression(node, target);
		
		if (node?.Type is UntypedType)
			node = MaterializeExpression(node,
				target is BorrowType { IsMutable: false } borrow ? borrow.Target : target);
		
		return ApplyImplicitConversion(node, target);
	}
	
	private static IResolvedExpressionNode Decay(IResolvedExpressionNode node) =>
		node is not ResolvedBorrowExpressionNode { IsImplicit: false } && node.Type is BorrowType borrow
			? new ResolvedUnaryOpExpressionNode(node, new NativeImpl(TokenType.OpStar, borrow.Target), node.Syntax)
			: node;
	
	private IResolvedExpressionNode ApplyArgumentConversion(IResolvedExpressionNode arg, FunctionConversion conversion,
		TypeSymbol target)
	{
		var converted = ApplyUserConversion(arg, conversion);
		return arg is ResolvedMutArgumentExpressionNode argument &&
		       conversion.Function.Symbol.Kind != FunctionKind.Free &&
		       conversion.Function.Signature.ReturnType is BorrowType && !IsInvalid(converted)
			? ApplyMutArgument(new ResolvedMutArgumentExpressionNode(converted,
				_typePool.GetPointerType(converted.Type), argument.Syntax), target)
			: converted;
	}
	
	private FunctionConversion? FindUserConversion(IResolvedExpressionNode source, TypeSymbol target,
		bool isExplicit = false)
	{
		var (value, use) = source switch
		{
			ResolvedMutArgumentExpressionNode argument => (Decay(argument.Place), ParameterMode.Mut),
			ResolvedOwnExpressionNode owned => (Decay(owned.Value), ParameterMode.Own),
			_ => (Decay(source), ParameterMode.ReadOnly)
		};
		
		return IsInvalid(value)
			? null
			: FindUserConversion(value.Type, value as ResolvedLiteralExpressionNode, use, target, isExplicit);
	}
	
	private FunctionConversion? FindUserConversion(TypeSymbol type, ResolvedLiteralExpressionNode? literal,
		ParameterMode use, TypeSymbol target, bool isExplicit = false)
	{
		if (IsInvalid(type) || IsInvalid(target))
			return null;
		
		List<(FunctionInfo Info, int Cost)> candidates =
		[
			..FindOutwardConversions(type, use, target, isExplicit).Select(static info => (Info: info, Cost: 0)),
			..use == ParameterMode.Mut
				? []
				: GetInwardConversions(target)
					.Where(static info => info.Signature.GetMode(0) != ParameterMode.Mut)
					.Select(info => InferConversion(info, type, literal))
					.OfType<FunctionInfo>()
					.Select(info => (Info: info,
						Cost: MatchConversionSource(type, literal, info.Signature.ParameterTypes[0])))
					.Where(static candidate => candidate.Cost < int.MaxValue)
		];
		
		if (candidates.Count == 0)
			return null;
		
		var cheapest = candidates.Min(static candidate => candidate.Cost);
		var best = candidates.Where(candidate => candidate.Cost == cheapest).ToList();
		return new FunctionConversion(best[0].Info, isExplicit ? ConversionKind.Explicit : ConversionKind.Implicit,
			UserConversionCost + cheapest)
		{
			IsAmbiguous = best.Count > 1
		};
	}
	
	private int MatchConversionSource(TypeSymbol type, ResolvedLiteralExpressionNode? literal, TypeSymbol parameter)
	{
		if (type == parameter)
			return 0;
		
		return type is UntypedType untyped && (literal is null || LiteralFits(literal, parameter))
			? untyped.MaterializationCost(parameter, MaterializationMode.Overload)
			: int.MaxValue;
	}
	
	private List<FunctionInfo> FindOutwardConversions(TypeSymbol type, ParameterMode use, TypeSymbol target,
		bool isExplicit)
	{
		if (type is UntypedType)
			return [];
		
		var matching = GetMethods(type, "as")
			.Concat(isExplicit ? GetMethods(type, "new") : [])
			.Where(method => method.Function is { IsConversion: true, Kind: FunctionKind.Method } &&
			                 CanAccess(type, method.Function))
			.Select(method => GetFunctionInfo(method, type))
			.Where(info => info.Signature.ParameterTypes.Length == 1 &&
			               Produces(info.Signature.ReturnType, target, use == ParameterMode.Mut))
			.ToList();
		
		ParameterMode[] modes = use switch
		{
			ParameterMode.Mut => [ParameterMode.Mut],
			ParameterMode.Own => [ParameterMode.Own, ParameterMode.ReadOnly, ParameterMode.Imm],
			_ => [ParameterMode.ReadOnly, ParameterMode.Imm, ParameterMode.Own]
		};
		
		return modes
			.Select(mode => matching.Where(info => info.Signature.GetMode(0) == mode).ToList())
			.FirstOrDefault(static infos => infos.Count > 0) ?? [];
	}
	
	private static bool Produces(TypeSymbol result, TypeSymbol target, bool isMutable) => isMutable
		? result is BorrowType { IsMutable: true } borrow && borrow.Target == target
		: result == target || result is BorrowType { IsMutable: false } view && view.Target == target;
	
	private IEnumerable<FunctionInfo> GetInwardConversions(TypeSymbol target) =>
		target is BorrowType or UntypedType or NamedTypeSymbol { IsGenericDefinition: true }
			? []
			: GetMethods(target, "as")
				.Where(method => method.Function is { IsConversion: true, Kind: FunctionKind.Free } &&
				                 CanAccess(target, method.Function))
				.Select(method => GetFunctionInfo(method, target))
				.Where(info => info.Signature.ParameterTypes.Length == 1 && info.Signature.ReturnType == target);
	
	private ResolutionSet ResolveConstruction(ICallable[] candidates, IResolvedExpressionNode[] args,
		TypeSymbol? target)
	{
		var construction = candidates
			.Where(static candidate => candidate is not ConversionCallable { Info.Symbol.Kind: FunctionKind.Free })
			.ToArray();
		
		if (construction.Length < candidates.Length)
		{
			var constructed = ResolveCallable(construction, args, MaterializationMode.Overload, target);
			if (constructed.IsAmbiguous || constructed.HasResult && constructed[0].Callable is not ConversionCallable)
				return constructed;
		}
		
		return ResolveCallable(candidates, args, MaterializationMode.Overload, target);
	}
	
	private ConversionCallable[] FindConversionCallables(IReadOnlyList<IResolvedExpressionNode> args,
		TypeSymbol target)
	{
		if (args is not [var arg] || IsInvalid(arg) || IsInvalid(target) ||
		    target is NamedTypeSymbol { IsGenericDefinition: true })
			return [];
		
		if (arg is ResolvedMutArgumentExpressionNode argument)
			return
			[
				..GetInwardConversions(target)
					.Where(static info => info.Signature.GetMode(0) == ParameterMode.Mut)
					.Select(info => InferConversion(info, Decay(argument.Place).Type, null))
					.OfType<FunctionInfo>()
					.Select(info => new ConversionCallable(info, target))
			];
		
		var (value, use) = arg is ResolvedOwnExpressionNode owned
			? (Decay(owned.Value), ParameterMode.Own)
			: (Decay(arg), ParameterMode.ReadOnly);
		
		return
		[
			..FindOutwardConversions(value.Type, use, target, true)
				.Concat(GetInwardConversions(target)
					.Select(info => InferConversion(info, value.Type, value as ResolvedLiteralExpressionNode))
					.OfType<FunctionInfo>())
				.Select(info => new ConversionCallable(info, target))
		];
	}
	
	private IResolvedExpressionNode ApplyUserConversion(IResolvedExpressionNode source, FunctionConversion conversion,
		IExpressionNode? syntax = null, TypeSymbol? target = null)
	{
		if (!conversion.IsAmbiguous)
			return CallConversion(conversion.Function, source, syntax ?? source.Syntax, target);
		
		var from = Decay(source is ResolvedMutArgumentExpressionNode argument ? argument.Place : source).Type;
		var to = conversion.To is BorrowType borrow ? borrow.Target : conversion.To;
		return Error(syntax ?? source.Syntax, $"Conversion from '{from.Name}' to '{to.Name}' is ambiguous", to);
	}
	
	private static bool PassesOwnParameter(IResolvedExpressionNode source, FunctionInfo info) => source switch
	{
		ResolvedOwnExpressionNode owned => PassesOwnParameter(owned.Value, info),
		ResolvedMutArgumentExpressionNode argument => PassesOwnParameter(argument.Place, info),
		ResolvedVarExpressionNode { Symbol: ParameterSymbol parameter } => info.Symbol.Parameters.Contains(parameter),
		_ => false
	};
	
	private IResolvedExpressionNode CallConversion(FunctionInfo info, IResolvedExpressionNode source,
		IExpressionNode syntax, TypeSymbol? target = null)
	{
		if (CurrentResolutionContext.ContainingFunction?.Symbol == info.Symbol && PassesOwnParameter(source, info))
			Diagnostics.Add(new(DiagnosticSeverity.Warning, syntax.SourceLocation,
				$"Conversion '{info.Symbol.DeclaredName}' calls itself") { Lint = Lints.SelfConversion });
		
		TrackFunctionUse(info, syntax);
		if (info.Symbol.Kind == FunctionKind.Free)
		{
			var value = source switch
			{
				{ Type: UntypedType } => MaterializeExpression(source, info.Signature.ParameterTypes[0]),
				ResolvedMutArgumentExpressionNode argument =>
					ApplyMutArgument(argument, info.Signature.ParameterTypes[0]),
				ResolvedOwnExpressionNode => source,
				_ => Decay(source)
			};
			
			return new ResolvedFunctionCallExpressionNode(info, [value], syntax) { IsConversion = true };
		}
		
		var receiver = source switch
		{
			ResolvedMutArgumentExpressionNode argument => Decay(argument.Place),
			ResolvedOwnExpressionNode => source,
			_ => Decay(source)
		};
		
		var call = new ResolvedFunctionCallExpressionNode(info, [CreateReceiver(receiver, info)], syntax)
		{
			IsConversion = true
		};
		
		return target is BorrowType && call.Type == target ? call : Decay(call);
	}
}