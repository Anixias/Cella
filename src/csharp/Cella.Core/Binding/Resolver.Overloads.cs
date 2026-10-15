using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private ResolutionSet ResolveCallable(IEnumerable<ICallable> candidates,
		IReadOnlyList<IResolvedExpressionNode> args, MaterializationMode mode, TypeSymbol? target = null,
		bool ignoreModes = false)
	{
		var options = new List<CallableResolution>();
		
		foreach (var candidate in candidates)
		{
			var parameterCount = candidate.ParameterTypes.Length;
			if (candidate.IsVariadic ? args.Count < parameterCount : args.Count != parameterCount)
				continue;
			
			if (!ignoreModes && args.Skip(parameterCount).Any(static arg => arg is ResolvedMutArgumentExpressionNode))
				continue;
			
			var conversionCost = 0;
			var distance = 0;
			var materializationCost = 0;
			var argumentConversions = new Conversion?[args.Count];
			var valid = true;
			
			for (var i = 0; i < parameterCount; i++)
			{
				var (cost, conversion) = MatchArg(args[i], candidate.ParameterTypes[i], candidate.GetMode(i), mode,
					ignoreModes);
				
				if (cost == int.MaxValue || candidate is ConversionCallable && conversion is not null)
				{
					valid = false;
					break;
				}
				
				if (args[i].Type is UntypedType)
					materializationCost += cost;
				else
				{
					conversionCost += cost;
					distance += conversion?.Distance ?? 0;
				}
				
				argumentConversions[i] = conversion;
			}
			
			if (!valid)
				continue;
			
			var resultRank = 0;
			var resultCost = 0;
			Conversion? resultConversion = null;
			
			if (target is not null && candidate.ReturnType != target && candidate.ReturnType is not NeverType)
			{
				var conversion = _conversionTable.FindImplicit(candidate.ReturnType, target) ??
				                 (candidate.ReturnType is BorrowType
					                 ? null
					                 : FindUserConversion(candidate.ReturnType, null, ParameterMode.ReadOnly, target));
				
				if (conversion is null && candidate.ReturnType != NativeSymbols.Void &&
				    candidate.ReturnType is not BorrowType && target is not BorrowType)
					continue;
				
				// Exact returns beat converted returns, so set to a higher cost
				resultRank = candidate.ReturnType == NativeSymbols.Void ? VoidResultRank : 1;
				resultCost = conversion?.Cost ?? 0;
				resultConversion = conversion;
			}
			
			var totalCost = new CallableCost(resultRank, conversionCost, distance, materializationCost, resultCost,
				candidate.IsGeneric ? 1 : 0);
			
			options.Add(new(candidate, totalCost, resultConversion, argumentConversions));
		}
		
		if (options.Count == 0)
			return ResolutionSet.None;
		
		var best = options.Min(static v => v.Cost);
		var winners = options.Where(v => v.Cost == best).ToList();
		return winners.Count switch
		{
			0 => ResolutionSet.None,
			1 => ResolutionSet.Single(winners[0]),
			_ => ResolutionSet.Ambiguous(winners)
		};
	}
	
	private List<IResolvedExpressionNode> ApplyArgumentResolution(IReadOnlyList<IResolvedExpressionNode> args,
		CallableResolution resolution)
	{
		var result = new List<IResolvedExpressionNode>(args.Count);
		
		for (var i = 0; i < args.Count; i++)
		{
			var arg = args[i];
			if (i >= resolution.Callable.ParameterTypes.Length)
			{
				result.Add(PromoteVariadicArgument(arg));
				continue;
			}
			
			var target = resolution.Callable.ParameterTypes[i];
			if (resolution.Callable.GetMode(i) == ParameterMode.Imm && target is not BorrowType &&
			    arg is ResolvedBorrowExpressionNode { IsMutable: false, IsImplicit: false } written)
				arg = written.Place;
			
			if (resolution.ArgumentConversions[i] is FunctionConversion userConversion)
			{
				result.Add(ApplyArgumentConversion(arg, userConversion, target));
				continue;
			}
			
			if (arg is ResolvedMutArgumentExpressionNode argument)
			{
				result.Add(ApplyMutArgument(argument, target));
				continue;
			}
			
			if (arg is ResolvedCaseNameExpressionNode caseName)
				arg = ResolveCaseName(caseName,
					target is BorrowType { IsMutable: false, Target: var readTarget } ? readTarget : target);
			
			if (IsDynTarget(target) && arg.Type != target && ConvertToDyn(arg, target) is { } erased)
			{
				result.Add(erased);
				continue;
			}
			
			var borrowed = target is BorrowType { IsMutable: false } borrow && arg.Type is not BorrowType
				? borrow
				: null;
			
			if (arg.Type is UntypedType)
				arg = MaterializeExpression(arg, borrowed?.Target ?? target);
			
			if (arg.Type is UntypedType)
				arg = MaterializeAsDefault(arg);
			
			if (borrowed is not null)
				arg = new ResolvedBorrowExpressionNode(arg, borrowed, true, arg.Syntax);
			else if (arg.Type is BorrowType && target is not BorrowType)
				arg = Decay(arg);
			
			if (target is FStrType fstr && arg.Type == NativeSymbols.Str)
				arg = CreateTextTemplate(arg, fstr);
			
			if (resolution.ArgumentConversions[i] is { } conversion)
				arg = new ResolvedConversionExpressionNode(arg, conversion, arg.Syntax);
			
			result.Add(arg);
		}
		
		return result;
	}
	
	private IResolvedExpressionNode ApplyMutArgument(ResolvedMutArgumentExpressionNode argument, TypeSymbol target)
	{
		var declared = target switch
		{
			PointerType { BaseType: var baseType } => baseType,
			BorrowType { Target: var borrowed } => borrowed,
			_ => argument.Place.Type
		};
		
		if (FindOpenPlace(argument.Place) is { } open)
		{
			RecordUse(open, declared, LocalUseKind.Read);
			RecordUse(open, declared, LocalUseKind.Write);
		}
		
		var place = argument.Place.Type == declared ? argument.Place : Decay(argument.Place);
		if (declared is DynType && place.Type != declared && !IsInvalid(place))
			return Erase(target is BorrowType
					? new ResolvedBorrowExpressionNode(place, _typePool.GetBorrowType(place.Type, true), false,
						argument.Syntax)
					: new ResolvedMutArgumentExpressionNode(place, _typePool.GetPointerType(place.Type),
						argument.Syntax),
				target);
		
		if (target is BorrowType borrow)
			return new ResolvedBorrowExpressionNode(place, borrow, false, argument.Syntax);
		
		return place == argument.Place
			? argument
			: new ResolvedMutArgumentExpressionNode(place, _typePool.GetPointerType(place.Type), argument.Syntax);
	}
	
	private IResolvedExpressionNode PromoteVariadicArgument(IResolvedExpressionNode arg)
	{
		if (arg.Type is UntypedStringType or InterpolatedStringType)
			arg = MaterializeExpression(arg, NativeSymbols.CStr);
		
		if (arg.Type is UntypedType)
			arg = MaterializeAsDefault(arg);
		
		arg = Decay(arg);
		switch (arg.Type)
		{
			case NeverType:
				return arg;
			
			case PrimitiveType { Kind: PrimitiveTypeKind.Bool }:
				return new ResolvedConversionExpressionNode(arg, _boolPromotion, arg.Syntax);
			
			case IntegerType type when CountBits(type) < 32:
				return new ResolvedConversionExpressionNode(arg,
					_conversionTable.FindExplicit(type, NativeSymbols.Int32)!,
					arg.Syntax);
			
			case IntegerType type when CountBits(type) <= 64:
				return arg;
			
			case FloatType { Kind: PrimitiveTypeKind.Float32 } type:
				return new ResolvedConversionExpressionNode(arg,
					_conversionTable.FindImplicit(type, NativeSymbols.Float64)!,
					arg.Syntax);
			
			case FloatType { Kind: PrimitiveTypeKind.Float64 }:
				return arg;
			
			case PointerType:
			case PrimitiveType { Kind: PrimitiveTypeKind.CStr }:
			case FunctionType { IsExternal: true }:
				return arg;
			
			case EnumSymbol { IsExternal: true, HasPayload: false } enumType when
				_conversionTable.FindExplicit(enumType, _typePool.GetTagType(enumType)) is { } toTag:
				return PromoteVariadicArgument(new ResolvedConversionExpressionNode(arg, toTag, arg.Syntax));
			
			case EnumSymbol { IsMatch: true } enumType when _typePool.GetMatchedType(enumType) is PointerType matched &&
			                                                _conversionTable.FindExplicit(enumType, matched) is
				                                                { } unwrap:
				return new ResolvedConversionExpressionNode(arg, unwrap, arg.Syntax);
			
			case var type when TypePool.ContainsInvalid(type):
				return arg;
			
			default:
				return Error(arg.Syntax, $"Cannot pass '{arg.Type.Name}' values to variadic functions",
					NativeSymbols.Invalid);
		}
	}
	
	private uint CountBits(TypeSymbol type) => (uint)_typePool.SizeTable.GetSize(type).CountBits(_pointerBitSize);
	
	private IResolvedExpressionNode ApplyResultResolution(IResolvedExpressionNode node,
		CallableResolution resolution) => resolution.ResultConversion switch
	{
		FunctionConversion userConversion => ApplyUserConversion(node, userConversion),
		{ } conversion => new ResolvedConversionExpressionNode(node, conversion, node.Syntax),
		null => node
	};
	
	private (int Cost, Conversion? Conversion) MatchArg(IResolvedExpressionNode arg, TypeSymbol target,
		ParameterMode parameterMode, MaterializationMode mode, bool ignoreModes)
	{
		var mutTarget = arg.Type is BorrowType ? null : GetMutTarget(target, parameterMode);
		if (parameterMode == ParameterMode.Mut || mutTarget is not null)
		{
			if (arg is ResolvedMutArgumentExpressionNode { Place: var place } && mutTarget is not null &&
			    FindOpenPlace(place) is not null)
				return (0, null);
			
			if (arg is ResolvedMutArgumentExpressionNode mutArgument && mutTarget is not null &&
			    IsMutPlaceOf(mutArgument.Place, mutTarget))
				return (mutArgument.Place.Type == mutTarget ? 0 : 1, null);
			
			if (arg is ResolvedMutArgumentExpressionNode && mutTarget is not null &&
			    FindUserConversion(arg, mutTarget) is { } mutConversion)
				return (mutConversion.Cost, mutConversion);
			
			return ignoreModes ? (0, null) : (int.MaxValue, null);
		}
		
		if ((parameterMode == ParameterMode.Imm || ignoreModes) && target is not BorrowType &&
		    arg is ResolvedBorrowExpressionNode { IsMutable: false, IsImplicit: false } borrowed)
			return MatchArg(borrowed.Place, target, mode);
		
		if (arg is not ResolvedMutArgumentExpressionNode argument)
			return ignoreModes && NeedsLending(arg, target, parameterMode)
				? (FindLendingConversion(arg, target)!.Cost, null)
				: MatchArg(arg, target, mode);
		
		if (parameterMode.IsReadOnly() && FindLendingConversion(argument.Place, target) is { } lending)
			return (lending.Cost, lending);
		
		return ignoreModes ? MatchArg(argument.Place, target, mode) : (int.MaxValue, null);
	}
	
	private bool NeedsLending(IResolvedExpressionNode arg, TypeSymbol target, ParameterMode parameterMode) =>
		parameterMode.IsReadOnly() && MatchArg(arg, target, MaterializationMode.Overload).Cost ==
		int.MaxValue && FindLendingConversion(arg, target) is not null;
	
	private FunctionConversion? FindLendingConversion(IResolvedExpressionNode place, TypeSymbol target)
	{
		var type = Decay(place).Type;
		if (IsInvalid(type) || IsInvalid(target) || type is UntypedType)
			return null;
		
		var isOpen = FindOpenPlace(Decay(place)) is not null;
		var conversions = GetInwardConversions(target)
			.Where(static info => info.Signature.GetMode(0) == ParameterMode.Mut)
			.Select(info => InferConversion(info, type, null))
			.OfType<FunctionInfo>()
			.Where(info => info.Signature.GetDeclaredType(0) is var source &&
			               (source == type || isOpen && HaveSameLength(source, type)))
			.ToList();
		
		return conversions.Count == 0
			? null
			: new FunctionConversion(conversions[0], ConversionKind.Implicit, UserConversionCost)
			{
				IsAmbiguous = conversions.Count > 1
			};
	}
	
	private static bool HaveSameLength(TypeSymbol first, TypeSymbol second) =>
		first is ArrayType { LengthParameter: null } a && second is ArrayType { LengthParameter: null } b &&
		a.Length == b.Length;
	
	private (int Cost, Conversion? Conversion) MatchArg(IResolvedExpressionNode arg, TypeSymbol target,
		MaterializationMode mode)
	{
		if (arg is ResolvedCaseNameExpressionNode caseName)
			return MatchCaseName(caseName, target, mode);
		
		if (arg is ResolvedInterpolatedStringExpressionNode template && target is FStrType fstr &&
		    !HolesConform(template, fstr))
			return (int.MaxValue, null);
		
		if (arg.Type == target)
			return (0, null);
		
		if (IsDynTarget(target))
			return CanConvertToDyn(arg.Type is UntypedType ? GetDefaultType(arg) : arg.Type, target)
				? (1, null)
				: arg is ResolvedLiteralExpressionNode { Type: UntypedType untyped } literal &&
				  (target is BorrowType { IsMutable: false, Target: DynType inner } ? inner : target) is DynType dyn &&
				  FindLiteralConformer(literal, dyn) is { } conformer
					? (2 + untyped.MaterializationCost(conformer, MaterializationMode.Overload), null)
					: (int.MaxValue, null);
		
		if (target is BorrowType { IsMutable: false } borrow && arg.Type is not BorrowType)
		{
			var (cost, _) = MatchArg(arg, borrow.Target, mode);
			return arg.Type == borrow.Target || arg.Type is UntypedType && cost != int.MaxValue
				? (cost + 1, null)
				: (int.MaxValue, null);
		}
		
		if (target is not BorrowType && Decay(arg) is var decayedArg && decayedArg != arg)
		{
			var (cost, decayedConversion) = MatchArg(decayedArg, target, mode);
			return cost == int.MaxValue ? (cost, null) : (cost + 1, decayedConversion);
		}
		
		if (arg is ResolvedFunctionGroupExpressionNode group && target is FunctionType functionType)
			return FindFunction(group.Group, functionType) is { } function
				? ((function.Symbol.IsExternal == functionType.IsExternal ? 0 : 1) + (functionType.IsRef ? 1 : 0) +
				   (FunctionGroupType.Matches(function, functionType) ? 0 : 1), null)
				: (int.MaxValue, null);
		
		if (arg.Type is UntypedType u)
		{
			// Special case for literals: If target type cannot store the value, conversion is impossible
			if (arg is ResolvedLiteralExpressionNode literal && !LiteralFits(literal, target))
				return (int.MaxValue, null);
			
			var cost = u.MaterializationCost(target, mode);
			return cost == int.MaxValue && FindUserConversion(arg, target) is { } literalConversion
				? (literalConversion.Cost, literalConversion)
				: (cost, null);
		}
		
		if (target is FStrType && arg.Type == NativeSymbols.Str)
			return (1, null);
		
		if (_conversionTable.FindImplicit(arg.Type, target) is { } conversion)
			return (conversion.Cost, conversion);
		
		return FindUserConversion(arg, target) is { } userConversion
			? (userConversion.Cost, userConversion)
			: (int.MaxValue, null);
	}
	
	private (int Cost, Conversion? Conversion) MatchCaseName(ResolvedCaseNameExpressionNode caseName,
		TypeSymbol target, MaterializationMode mode)
	{
		var (read, borrowCost) = target is BorrowType { IsMutable: false } borrow ? (borrow.Target, 1) : (target, 0);
		if (read is EnumSymbol enumType && ChooseCase(enumType, caseName) != CaseChoice.Symbol)
			return (borrowCost + (IsViableCase(enumType, caseName) ? 0 : 1), null);
		
		return caseName.Symbol is null ? (int.MaxValue, null) : MatchArg(GetFallback(caseName), target, mode);
	}
	
	private bool LiteralFits(ResolvedLiteralExpressionNode literal, TypeSymbol target) => (literal.Type, target) switch
	{
		(UntypedIntegerType, IntegerType type) => IntegerValueIn(literal, type) is not null,
		(UntypedIntegerType, FloatType type) => IsExactInFloat(DefaultIntegerValue(literal), type),
		(UntypedFloatType, FloatType type) => FitsInFloat(literal.Value!, type),
		_ => true
	};
}