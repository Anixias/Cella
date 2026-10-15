using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Collections;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private TypeArgumentList? ResolveTypeArguments(IndexerExpressionNode node, IEnumerable<ICallable> candidates) =>
		ResolveTypeArguments(node, candidates.Select(static candidate => candidate switch
		{
			FunctionCallable callable => callable.Info,
			ReceiverCallable callable => callable.Info,
			_ => (FunctionInfo?)null
		}).OfType<FunctionInfo>());
	
	private TypeArgumentList? ResolveTypeArguments(IndexerExpressionNode node, IEnumerable<FunctionInfo> functions)
	{
		var context = CurrentResolutionContext;
		var candidates = functions.ToList();
		var shapes = candidates
			.Select(GetOpenTypeParameters)
			.Where(open => open.Length == node.Arguments.Length)
			.ToList();
		
		if (shapes.Count == 0 && candidates.Count > 1)
		{
			Diagnostics.Add(ReportOverloadArgumentCount(node.SourceLocation, GetName(node.Target),
				node.Arguments.Length));
			
			return null;
		}
		
		var types = node.Arguments
			.Select((argument, i) => context.ResolveGenericArgumentExpression(argument, FindSharedParameter(shapes, i)))
			.ToImmutableArray();
		
		return types.Any(static type => type is InvalidType)
			? null
			: new(types, [..node.Arguments.Select(static argument => argument.SourceLocation)], node.SourceLocation);
	}
	
	private static TypeParameterSymbol?
		FindSharedParameter(List<ImmutableArray<TypeParameterSymbol>> shapes, int index) =>
		shapes is [var first, ..] && shapes.All(shape => shape[index].IsValue == first[index].IsValue &&
		                                                 shape[index].IsTrait == first[index].IsTrait &&
		                                                 shape[index].ValueType == first[index].ValueType)
			? first[index]
			: null;
	
	private ICallable? Specialize(ICallable candidate, string name, IReadOnlyList<IResolvedExpressionNode> args,
		TypeArgumentList? typeArguments, SourceLocation location, List<Diagnostic> failures,
		List<Diagnostic>? violations = null)
	{
		FunctionInfo info;
		switch (candidate)
		{
			case FunctionCallable callable:
				info = callable.Info;
				break;
			
			case ReceiverCallable callable:
				info = callable.Info;
				break;
			
			default:
				return candidate;
		}
		
		var open = GetOpenTypeParameters(info);
		if (open.IsEmpty && typeArguments is null)
			return candidate;
		
		ImmutableArray<TypeSymbol> arguments;
		Func<int, SourceLocation> locate;
		if (typeArguments is { } explicitArguments)
		{
			if (explicitArguments.Types.Length != open.Length)
			{
				failures.Add(
					ResolutionContext.ReportGenericArgumentCount(explicitArguments.Location, name, open.Length));
				
				return null;
			}
			
			arguments = explicitArguments.Types;
			locate = i => explicitArguments.Locations[i];
		}
		else
		{
			var inputs = args
				.Take(candidate.ParameterTypes.Length)
				.Select((arg, i) => CreateInferenceInput(candidate.ParameterTypes[i], candidate.GetMode(i), arg));
			
			var result = _inference.Infer(open, inputs.OfType<InferenceInput>(), candidate.ReturnType,
				ExpectedType);
			
			if (!result.Succeeded)
			{
				failures.Add(ReportInferenceFailure(result, name, location));
				return null;
			}
			
			arguments = result.Arguments;
			locate = _ => location;
			MarkDefaultedArguments(open, candidate, args);
		}
		
		if (ReportConstraintViolation(open, arguments, locate, info) is { } violation)
		{
			failures.Add(violation);
			violations?.Add(violation);
			return null;
		}
		
		var instantiated = InstantiateDeclared(info, arguments);
		ICallable specialized = candidate is FunctionCallable
			? new FunctionCallable(instantiated, Repack(candidate, instantiated)) { IsGeneric = true }
			: new ReceiverCallable(instantiated, instantiated.Signature.ReturnType, Repack(candidate, instantiated))
			{
				IsGeneric = true
			};
		
		RecordGenericUses(args, specialized);
		return specialized;
	}
	
	private void RecordGenericUses(IReadOnlyList<IResolvedExpressionNode> args, ICallable instantiated)
	{
		if (survey is null)
			return;
		
		for (var i = 0; i < args.Count && i < instantiated.ParameterTypes.Length; i++)
			RecordMaterialization(args[i], instantiated.ParameterTypes[i]);
	}
	
	private void MarkDefaultedArguments(ImmutableArray<TypeParameterSymbol> open, ICallable candidate,
		IReadOnlyList<IResolvedExpressionNode> args)
	{
		if (survey is not { } activeSurvey)
			return;
		
		var count = Math.Min(args.Count, candidate.ParameterTypes.Length);
		foreach (var parameter in open)
		{
			if (ExpectedType is not null && MentionsParameter(candidate.ReturnType, parameter))
				continue;
			
			var inputs = Enumerable.Range(0, count)
				.Where(i => MentionsParameter(candidate.ParameterTypes[i], parameter))
				.Select(i => args[i])
				.ToList();
			
			if (inputs.All(static input => input.Type is UntypedType))
				inputs.Where(static input => input.Type is not FunctionGroupType).ToList()
					.ForEach(activeSurvey.MarkDefaulted);
		}
		
		static bool MentionsParameter(TypeSymbol type, TypeParameterSymbol parameter) =>
			TypePool.FindTypeParameters(type).Contains(parameter);
	}
	
	private FunctionInfo InstantiateLike(FunctionInfo info, FunctionInfo model)
	{
		var count = info.Symbol.DeclaredTypeParameters.Length;
		return GetOpenTypeParameters(info).IsEmpty || model.TypeArguments.Length < count
			? info
			: InstantiateDeclared(info, model.TypeArguments.TakeLast(count));
	}
	
	private FunctionInfo? InferConversion(FunctionInfo info, TypeSymbol type, ResolvedLiteralExpressionNode? literal)
	{
		var open = GetOpenTypeParameters(info);
		if (open.IsEmpty)
			return info;
		
		var parameter = info.Signature.ParameterTypes[0];
		IEnumerable<InferenceInput?> inputs =
		[
			literal is null
				? new InferenceInput(info.Signature.GetDeclaredType(0), type is FunctionGroupType
				{
					Functions: [var single]
				} && GetOpenTypeParameters(single).IsEmpty
					? GetNaturalType(single)
					: type)
				: CreateInferenceInput(parameter, info.Signature.GetMode(0), literal)
		];
		
		var result = _inference.Infer(open, inputs.OfType<InferenceInput>(), info.Signature.ReturnType, null);
		return result.Succeeded && ReportConstraintViolation(open, result.Arguments, static _ => default, info) is null
			? InstantiateDeclared(info, result.Arguments)
			: null;
	}
	
	private static ImmutableArray<TypeParameterSymbol> GetOpenTypeParameters(FunctionInfo info)
	{
		var declared = info.Symbol.DeclaredTypeParameters;
		if (declared.IsEmpty || info.TypeArguments.IsDefaultOrEmpty)
			return declared;
		
		var offset = info.Symbol.TypeParameters.Length - declared.Length;
		return [..declared.Where((parameter, i) => info.TypeArguments[offset + i] == parameter)];
	}
	
	private FunctionInfo InstantiateDeclared(FunctionInfo info, IEnumerable<TypeSymbol> arguments)
	{
		var symbol = info.Symbol;
		var outerCount = symbol.TypeParameters.Length - symbol.DeclaredTypeParameters.Length;
		var outer = info.TypeArguments.IsDefaultOrEmpty
			? symbol.TypeParameters.Take(outerCount)
			: info.TypeArguments.Take(outerCount);
		
		return _typePool.InstantiateFunction(info, [..outer, ..arguments]);
	}
	
	private InferenceInput? CreateInferenceInput(TypeSymbol parameter, ParameterMode mode, IResolvedExpressionNode arg)
	{
		if (mode == ParameterMode.Imm && parameter is not BorrowType &&
		    arg is ResolvedBorrowExpressionNode { IsMutable: false, IsImplicit: false } written)
			arg = written.Place;
		
		var type = GetArgumentType(arg);
		if (GetMutTarget(parameter, mode) is { } declared)
			return type is UntypedType or InvalidType
				? null
				: arg is ResolvedMutArgumentExpressionNode { Place: var place } && FindOpenPlace(place) is not null &&
				  FindLiteralType(type) is { } literal
					? new(declared, type) { Literal = literal }
					: new(declared, type) { IsExact = true };
		
		return arg switch
		{
			ResolvedFunctionGroupExpressionNode { Group.Functions: [var single] } when
				GetOpenTypeParameters(single).IsEmpty => new(parameter, GetNaturalType(single)),
			ResolvedFunctionGroupExpressionNode group when FindBoundFunction(parameter, group.Group) is { } bound =>
				new(parameter, GetNaturalType(bound)),
			ResolvedCaseNameExpressionNode => null,
			_ when type is NeverType or FunctionGroupType or InvalidType => null,
			_ when type is UntypedType literal => new(parameter, GetDefaultType(arg)) { Literal = literal },
			_ => ConvertForInference(parameter, type, arg is ResolvedMutArgumentExpressionNode) ?? new(parameter, type)
		};
	}
	
	private FunctionInfo? FindBoundFunction(TypeSymbol parameter, FunctionGroupType group) =>
		parameter is TypeParameterSymbol bounded &&
		_typePool.GetFunctionBounds(bounded)
			.Where(static bound => !TypePool.FindTypeParameters(bound).Any())
			.Select(bound => FindFunction(group, bound))
			.OfType<FunctionInfo>()
			.Distinct()
			.ToList() is [var found]
			? found
			: null;
	
	private InferenceInput? ConvertForInference(TypeSymbol parameter, TypeSymbol type, bool isMut)
	{
		var open = new OrderedSet<TypeParameterSymbol>(TypePool.FindTypeParameters(parameter));
		if (open.Count == 0 || _typePool.TryUnify(parameter, type, open, []))
			return null;
		
		var results = FindInwardResults(parameter, type, isMut, open)
			.Concat(isMut ? [] : FindOutwardResults(parameter, type, open))
			.Distinct()
			.ToList();
		
		return results is [var only] ? new InferenceInput(parameter, only) : null;
	}
	
	private IEnumerable<TypeSymbol> FindInwardResults(TypeSymbol parameter, TypeSymbol type, bool isMut,
		OrderedSet<TypeParameterSymbol> open)
	{
		foreach (var info in GetInwardConversions(parameter))
		{
			if (info.Signature.GetMode(0) == ParameterMode.Mut != isMut)
				continue;
			
			var variables = new OrderedSet<TypeParameterSymbol>([..open, ..GetOpenTypeParameters(info)]);
			var bindings = new Dictionary<TypeParameterSymbol, TypeSymbol>();
			if (_typePool.TryUnify(info.Signature.GetDeclaredType(0), type, variables, bindings) &&
			    MeetsConversionBounds(info, variables, bindings))
				yield return _typePool.Substitute(info.Signature.ReturnType, bindings);
		}
	}
	
	private bool MeetsConversionBounds(FunctionInfo info, OrderedSet<TypeParameterSymbol> variables,
		Dictionary<TypeParameterSymbol, TypeSymbol> bindings)
	{
		var map = info.TypeArguments.IsDefaultOrEmpty
			? new Dictionary<TypeParameterSymbol, TypeSymbol>()
			: TypePool.CreateMap(info.Symbol.TypeParameters, info.TypeArguments);
		
		foreach (var parameter in GetOpenTypeParameters(info))
		{
			if (!bindings.TryGetValue(parameter, out var argument))
				continue;
			
			foreach (var bound in _typePool.GetBounds(parameter).Select(bound => _typePool.SubstituteTrait(bound, map)))
			{
				var met = FindConformanceArguments(argument, bound.Trait).Any(arguments =>
					arguments.Length == bound.Arguments.Length && bound.Arguments.Zip(arguments)
						.All(pair => _typePool.TryUnify(pair.First, pair.Second, variables, bindings)));
				
				if (!met)
					return false;
			}
		}
		
		return true;
	}
	
	private IEnumerable<ImmutableArray<TypeSymbol>> FindConformanceArguments(TypeSymbol type, TraitSymbol trait) =>
		type is TypeParameterSymbol parameter
			? _typePool.GetBounds(parameter).Where(bound => bound.Trait == trait)
				.Select(static bound => bound.Arguments)
			: _typePool.FindConformances(type)
				.Where(conformance => conformance.Trait == trait)
				.Select(conformance => _typePool.GetConformanceArguments(conformance, type));
	
	private IEnumerable<TypeSymbol> FindOutwardResults(TypeSymbol parameter, TypeSymbol type,
		OrderedSet<TypeParameterSymbol> open)
	{
		if (type is UntypedType)
			yield break;
		
		foreach (var method in GetMethods(type, "as"))
		{
			if (method.Function is not { IsConversion: true, Kind: FunctionKind.Method } ||
			    !CanAccess(type, method.Function))
				continue;
			
			var info = GetFunctionInfo(method, type);
			if (info.Signature is not { ParameterTypes.Length: 1 } signature ||
			    signature.GetMode(0) == ParameterMode.Mut || !GetOpenTypeParameters(info).IsEmpty)
				continue;
			
			var result = signature.ReturnType is BorrowType { IsMutable: false } view
				? view.Target
				: signature.ReturnType;
			
			if (result is not BorrowType && _typePool.TryUnify(parameter, result, open, []))
				yield return result;
		}
	}
	
	private TypeSymbol GetDefaultType(IResolvedExpressionNode node) => node.Type switch
	{
		UntypedIntegerType when node is ResolvedLiteralExpressionNode literal =>
			FindDefaultIntegerType(literal) ?? NativeSymbols.Int32,
		UntypedIntegerType { IsUnsigned: true } => NativeSymbols.UInt32,
		UntypedIntegerType => NativeSymbols.Int32,
		UntypedFloatType => NativeSymbols.Float64,
		UntypedNullType => NativeSymbols.VoidPtr,
		UntypedStringType or InterpolatedStringType => NativeSymbols.Str,
		var type => type
	};
	
	private Diagnostic ReportInferenceFailure(InferenceResult result, string name, SourceLocation location)
	{
		var message = result.Conflicted is { } parameter
			? HoldsDistinctClosures(result.WrittenCandidates)
				? $"Cannot infer '{parameter.Name}' for '{name}' from different 'own fun' closures"
				: $"Cannot infer '{parameter.Name}' for '{name}' from {JoinNames(result.WrittenCandidates)}"
			: $"Cannot infer {JoinNames(result.Missing)} for '{name}'";
		
		var diagnostic = new Diagnostic(DiagnosticSeverity.Error, location, message);
		_inferenceFailures.Add(diagnostic);
		return diagnostic;
	}
	
	private static string JoinTypeNames(IEnumerable<IResolvedExpressionNode> values) =>
		string.Join(", ", values.Select(static v => $"'{v.Type.Name}'").Distinct());
	
	private static bool HoldsDistinctClosures(IEnumerable<TypeSymbol> types)
	{
		var distinct = types.Distinct().ToList();
		return distinct.Count > 1 && distinct.Any(ContainsClosure) &&
		       distinct.Select(static type => type.Name).Distinct().Count() < distinct.Count;
	}
	
	private static bool ContainsClosure(TypeSymbol type) => type switch
	{
		ClosureType => true,
		PointerType pointer => ContainsClosure(pointer.BaseType),
		BorrowType borrow => ContainsClosure(borrow.Target),
		ArrayType array => ContainsClosure(array.ElementType),
		_ => type.TypeArguments.Any(ContainsClosure)
	};
	
	private static string JoinNames(IEnumerable<TypeSymbol> types) =>
		DiagnosticReporter.JoinNames([..types.Select(static type => type.Name)]);
	
	private static readonly string[] _literalKeywords = ["true", "false", "null"];
	
	private static string? DescribeLiteralMismatch(IResolvedExpressionNode source, TypeSymbol target)
	{
		if (source is not ResolvedLiteralExpressionNode { Type: IntegerType or FloatType } literal ||
		    literal.Type == NativeSymbols.Char || !IsUnsuffixedNumber(literal.Syntax))
			return null;
		
		var text = literal.Syntax.SourceLocation.GetText().ToString();
		return (literal.Type, target) switch
		{
			(IntegerType, IntegerType) or (FloatType, FloatType) => $"'{text}' doesn't fit in '{target.Name}'",
			(IntegerType, FloatType) => $"'{text}' can't be represented exactly in '{target.Name}'",
			_ => null
		};
	}
	
	private static bool IsShift(TokenType op) => op is TokenType.OpLessLess or TokenType.OpGreaterGreater or
		TokenType.OpLessLessLess or TokenType.OpGreaterGreaterGreater;
	
	private static string JoinWrittenNames(IEnumerable<TypeSymbol> types) =>
		DiagnosticReporter.JoinNames([
			..types.Select(static type => type is FunctionType function
				? function.PlainName
				: type.Name)
		]);
	
	private Diagnostic? ReportConstraintViolation(ImmutableArray<TypeParameterSymbol> parameters,
		IReadOnlyList<TypeSymbol> arguments, Func<int, SourceLocation> locate, FunctionInfo? owner = null)
	{
		var map = TypePool.CreateMap(parameters, arguments);
		if (owner is { TypeArguments.IsDefaultOrEmpty: false } info)
		{
			var outer = info.Symbol.TypeParameters.Length - info.Symbol.DeclaredTypeParameters.Length;
			for (var i = 0; i < outer; i++)
				map.TryAdd(info.Symbol.TypeParameters[i], info.TypeArguments[i]);
		}
		
		for (var i = 0; i < parameters.Length; i++)
		{
			if (_typePool.FindConstraintViolation(parameters[i], arguments[i], map) is { } violation)
				return violation.At(locate(i));
		}
		
		return null;
	}
}