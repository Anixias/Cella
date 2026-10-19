using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private IResolvedExpressionNode[] ResolveArguments(ImmutableArray<IExpressionNode> nodes,
		IReadOnlyList<CallShape> shapes, out bool isAmbiguous,
		Func<IResolvedExpressionNode[], bool>? succeeds = null,
		Func<int, IResolvedExpressionNode[], bool>? accepts = null, IResolvedExpressionNode? first = null)
	{
		var values = ResolveArgumentValues(nodes, shapes, out isAmbiguous, succeeds, accepts, first);
		if (_callArguments.Count > 0)
			_callArguments[^1] = values;
		
		return values;
	}
	
	private IResolvedExpressionNode[] ResolveArgumentValues(ImmutableArray<IExpressionNode> nodes,
		IReadOnlyList<CallShape> shapes, out bool isAmbiguous, Func<IResolvedExpressionNode[], bool>? succeeds,
		Func<int, IResolvedExpressionNode[], bool>? accepts, IResolvedExpressionNode? first)
	{
		isAmbiguous = false;
		var values = new IResolvedExpressionNode?[nodes.Length];
		var deferred = new List<int>();
		for (var i = 0; i < nodes.Length; i++)
		{
			if (i == 0 && first is not null)
				values[i] = first;
			else if (IsDeferred(nodes[i]))
				deferred.Add(i);
			else
				values[i] = VisitArgument(nodes[i], ParameterTypesAt(shapes, i));
		}
		
		if (deferred.Count == 0)
			return values!;
		
		var checkpoint = OpenCheckpoint();
		foreach (var i in deferred)
			values[i] = nodes[i] is LambdaExpressionNode lambda
				? VisitLambdaArgument(lambda, i, shapes, values)
				: VisitDeferred(nodes[i], i, shapes,
				[
					..shapes.Select(shape =>
						ExpectedAt(shape, values, i) ?? ExpectArrayLiteral(shape, values, i, nodes[i]) ??
						ExpectLocalLambda(shape, values, i, nodes[i]))
				]);
		
		var resolved = values.Select(static value => value!).ToArray();
		if (succeeds is null || accepts is null || AnyInvalid(resolved) ||
		    shapes.Count < 2 && shapes.All(static shape => shape.Open.IsEmpty) ||
		    succeeds(resolved) && (shapes.Count < 2 ||
		                           !deferred.Any(i => resolved[i] is ResolvedCaseNameExpressionNode)))
		{
			Commit(checkpoint);
			return resolved;
		}
		
		var retries = FindRetries(nodes, shapes, resolved, deferred, accepts, true);
		if (retries.Count == 0)
			retries = FindRetries(nodes, shapes, resolved, deferred, accepts, false);
		
		var cheapest = retries.Select(static retry => retry.Cost).DefaultIfEmpty().Min();
		var best = retries.Where(retry => retry.Cost == cheapest).ToList();
		if (best is not [var (shape, hints, _)])
		{
			Commit(checkpoint);
			isAmbiguous = best.Select(static retry => retry.Shape).Distinct().Count() > 1;
			return resolved;
		}
		
		Rollback(checkpoint);
		foreach (var i in deferred)
		{
			resolved[i] = nodes[i] is LambdaExpressionNode lambda
				? VisitLambdaArgument(lambda, i, [shapes[shape]], resolved)
				: VisitDeferred(nodes[i], i, [shapes[shape]], [hints[i]]);
			
			if (resolved[i] is ResolvedCaseNameExpressionNode && (hints[i] ?? ExpectedAt(shapes[shape], resolved, i)) is
			    EnumSymbol target)
				resolved[i] = MaterializeExpression(resolved[i], target);
		}
		
		return resolved;
	}
	
	private IResolvedExpressionNode VisitDeferred(IExpressionNode node, int index, IReadOnlyList<CallShape> shapes,
		IReadOnlyList<TypeSymbol?> expected)
	{
		if (FindOpenCaseEnum(node, index, shapes, expected) is { } definition)
		{
			var call = (CallExpressionNode)node;
			var callee = ((VarExpressionNode)call.Target).Identifier;
			return VisitInferredEnumCase(callee, definition.Name, callee.SourceLocation, definition, call, node);
		}
		
		var types = shapes
			.Select((shape, i) => expected[i] ?? (index < shape.Parameters.Count ? shape.Parameters[index] : null))
			.OfType<TypeSymbol>();
		
		var hint = expected.All(static type => type is not null) && expected.Distinct().Count() == 1
			? expected[0]
			: null;
		
		return VisitArgument(node, types, hint is BorrowType { IsMutable: false } borrow ? borrow.Target : hint);
	}
	
	private EnumSymbol? FindOpenCaseEnum(IExpressionNode node, int index, IReadOnlyList<CallShape> shapes,
		IReadOnlyList<TypeSymbol?> expected)
	{
		if (node is not CallExpressionNode { Target: VarExpressionNode callee } ||
		    CurrentResolutionContext.Resolve(callee.Identifier.Text) is not null)
			return null;
		
		var definitions = shapes
			.Select((shape, i) => expected[i] is null && index < shape.Parameters.Count &&
			                      shape.Parameters[index] is EnumSymbol { IsGenericInstance: true } enumType &&
			                      HasCase(enumType, callee.Identifier.Text) &&
			                      TypePool.FindTypeParameters(enumType).Any(shape.Open.Contains)
				? (EnumSymbol)enumType.OriginalDefinition
				: null)
			.Distinct()
			.ToList();
		
		return definitions is [{ } definition] ? definition : null;
	}
	
	private TypeSymbol? ExpectArrayLiteral(CallShape shape, IReadOnlyList<IResolvedExpressionNode?> values, int index,
		IExpressionNode node)
	{
		if (node is not ArrayExpressionNode literal || index >= shape.Parameters.Count ||
		    shape.Modes[index] == ParameterMode.Mut)
			return null;
		
		return _typePool.Substitute(shape.Parameters[index], GetKnownArguments(shape, values)) is ArrayType
		{
			LengthParameter: { } length, ElementType: var element
		} && shape.Open.Contains(length) && !TypePool.FindTypeParameters(element).Any(shape.Open.Contains)
			? _typePool.GetArrayType(element, literal.Values.Length)
			: null;
	}
	
	private FunctionType? ExpectLocalLambda(CallShape shape, IReadOnlyList<IResolvedExpressionNode?> values, int index,
		IExpressionNode node) =>
		node is VarExpressionNode name && FindOpenLocal(name) is { Initializer: LambdaExpressionNode lambda }
			? ExpectLambda(shape, values, index, ResolveAnnotations(lambda))
			: null;
	
	private List<(int Shape, TypeSymbol?[] Hints, int Cost)> FindRetries(ImmutableArray<IExpressionNode> nodes,
		IReadOnlyList<CallShape> shapes, IResolvedExpressionNode[] values, List<int> deferred,
		Func<int, IResolvedExpressionNode[], bool> accepts, bool checksBodies)
	{
		var settled = values.Select((value, i) => deferred.Contains(i) ? null : value).ToArray();
		var retries = new List<(int Shape, TypeSymbol?[] Hints, int Cost)>();
		for (var index = 0; index < shapes.Count; index++)
		{
			foreach (var hints in FindHintOptions(shapes[index], values, settled, deferred))
			{
				if (!retries.Any(retry => retry.Hints.SequenceEqual(hints)) && Fit(index, hints) is { } cost)
					retries.Add((index, hints, cost));
			}
		}
		
		return retries;
		
		int? Fit(int index, TypeSymbol?[] hints)
		{
			var checkpoint = OpenCheckpoint();
			try
			{
				var trial = values.ToArray();
				foreach (var i in deferred)
					trial[i] = nodes[i] is LambdaExpressionNode lambda
						? VisitLambdaArgument(lambda, i, [shapes[index]], trial, true, checksBodies)
						: VisitDeferred(nodes[i], i, [shapes[index]], [hints[i]]);
				
				return AnyInvalid(trial) || checkpoint.HasErrors || !accepts(index, trial)
					? null
					: deferred
						.Select(i => nodes[i] is LambdaExpressionNode
							? FindLambdaDistance(nodes[i], trial, shapes[index], i) ?? 0
							: FindCaseDistance(nodes[i], values[i], trial[i], shapes[index], i) ??
							  LiteralDistance(values[i].Type, trial[i].Type))
						.Aggregate(0, AddCosts);
			}
			finally
			{
				Rollback(checkpoint);
			}
		}
	}
	
	private int? FindCaseDistance(IExpressionNode node, IResolvedExpressionNode value, IResolvedExpressionNode trial,
		CallShape shape, int index)
	{
		if (value is not ResolvedCaseNameExpressionNode)
			return null;
		
		var chosen = trial.Type is EnumSymbol ? trial.Type
			: index < shape.Parameters.Count && shape.Open.IsEmpty ? shape.Parameters[index]
			: null;
		
		if (chosen is not EnumSymbol enumType)
			return null;
		
		if (enumType is not { IsGenericInstance: true, OriginalDefinition: EnumSymbol definition } ||
		    node is not CallExpressionNode { Target: VarExpressionNode callee } call)
			return 0;
		
		var natural = VisitInferredEnumCase(callee.Identifier, definition.Name, callee.Identifier.SourceLocation,
			definition, call, node);
		
		return IsInvalid(natural) ? null : LiteralDistance(natural.Type, enumType);
	}
	
	private int? FindLambdaDistance(IExpressionNode node, IResolvedExpressionNode[] trial, CallShape shape,
		int index)
	{
		if (node is not LambdaExpressionNode { ExpressionBody: not null, ReturnType: null } lambda ||
		    ExpectLambda(shape, trial, index, ResolveAnnotations(lambda)) is not { } target)
			return null;
		
		var checkpoint = OpenCheckpoint();
		var natural = ResolveLambda(lambda, target, false, true, true, shape.Open).Type;
		var hasErrors = checkpoint.HasErrors;
		Rollback(checkpoint);
		return !hasErrors && natural is FunctionGroupType { Functions: [var function] }
			? LiteralDistance(function.Signature.ReturnType, target.ReturnType)
			: null;
	}
	
	private static int LiteralDistance(TypeSymbol natural, TypeSymbol chosen) => (natural, chosen) switch
	{
		_ when natural == chosen => 0,
		(NamedTypeSymbol n, NamedTypeSymbol c) when n.Definition == c.Definition =>
			n.TypeArguments.Zip(c.TypeArguments, LiteralDistance).Aggregate(0, AddCosts),
		(ArrayType n, ArrayType c) when n.Length == c.Length && n.LengthParameter == c.LengthParameter =>
			LiteralDistance(n.ElementType, c.ElementType),
		(BorrowType n, BorrowType c) when n.IsMutable == c.IsMutable => LiteralDistance(n.Target, c.Target),
		_ => FindLiteralType(natural)?.MaterializationCost(chosen, MaterializationMode.Overload) ?? int.MaxValue
	};
	
	private static UntypedType? FindLiteralType(TypeSymbol type) => type switch
	{
		IntegerType => UntypedIntegerType.Instance,
		FloatType => UntypedFloatType.Instance,
		StringType => UntypedStringType.Instance,
		PointerType => UntypedNullType.Instance,
		_ => null
	};
	
	private static int AddCosts(int first, int second) =>
		first == int.MaxValue || second == int.MaxValue ? int.MaxValue : first + second;
	
	private IEnumerable<TypeSymbol?[]> FindHintOptions(CallShape shape, IResolvedExpressionNode[] values,
		IResolvedExpressionNode?[] settled, List<int> deferred)
	{
		List<IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>?> choices = [null];
		var result = _inference.Infer(shape.Open, CreateInferenceInputs(shape, values), shape.ReturnType, ExpectedType);
		if (result.Conflicted is { } parameter)
			choices.AddRange(result.Candidates.Select(candidate =>
				new Dictionary<TypeParameterSymbol, TypeSymbol> { [parameter] = candidate }));
		
		foreach (var choice in choices)
		{
			var hints = new TypeSymbol?[values.Length];
			foreach (var i in deferred)
				hints[i] = ExpectedAt(shape, settled, i, choice);
			
			if (deferred.Any(i => hints[i] is not null))
				yield return hints;
		}
	}
	
	private TypeSymbol? ExpectedAt(CallShape shape, IReadOnlyList<IResolvedExpressionNode?> values, int index,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? chosen = null)
	{
		if (index >= shape.Parameters.Count || shape.Modes[index] == ParameterMode.Mut ||
		    shape.Parameters[index] is BorrowType { IsMutable: true })
			return null;
		
		var parameter = shape.Parameters[index];
		var open = TypePool.FindTypeParameters(parameter).Where(shape.Open.Contains).ToList();
		if (open.Count == 0)
			return parameter;
		
		var map = GetKnownArguments(shape, values, chosen);
		return open.All(map.ContainsKey) ? _typePool.Substitute(parameter, map) : null;
	}
	
	private Dictionary<TypeParameterSymbol, TypeSymbol> GetKnownArguments(CallShape shape,
		IReadOnlyList<IResolvedExpressionNode?> values,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? chosen = null)
	{
		var known = _inference.InferKnown(shape.Open, CreateInferenceInputs(shape, values), shape.ReturnType,
			ExpectedType, chosen);
		
		return shape.Open
			.Select((type, i) => (Parameter: type, Type: known[i]))
			.Where(static entry => entry.Type is not null)
			.ToDictionary(static entry => entry.Parameter, static entry => entry.Type!);
	}
	
	private IResolvedExpressionNode VisitLambdaArgument(LambdaExpressionNode node, int index,
		IReadOnlyList<CallShape> shapes, IReadOnlyList<IResolvedExpressionNode?> values, bool isTrial = false,
		bool checksBody = false)
	{
		var settled = values.Select((value, i) => i == index ? null : value).ToList();
		var annotations = ResolveAnnotations(node);
		if (annotations.Types.Any(static type => type is InvalidType) || annotations.ReturnType is InvalidType)
		{
			if (!isTrial)
				ReportAnnotations(node);
			
			return new ResolvedInvalidExpressionNode(node);
		}
		
		var expected = shapes
			.Select(shape => (Shape: shape, Type: ExpectLambda(shape, settled, index, annotations)))
			.Where(static entry => entry.Type is not null)
			.GroupBy(static entry => entry.Type!)
			.Select(static group => new LambdaTarget(group.Key,
				[..group.SelectMany(static entry => entry.Shape.Open).Distinct()]))
			.ToList();
		
		if (expected.Count == 0 && shapes.Any(shape => index < shape.Parameters.Count &&
		                                               shape.Parameters[index] is InvalidType))
			return ResolveLambda(node, NativeSymbols.Invalid, !isTrial || checksBody, true);
		
		var fitting = expected.Where(entry => Fits(annotations, entry.Type, entry.Open)).ToList();
		var target = fitting is [var single] ? single : expected is [var only] ? only : null;
		return target is null && expected.Count > 1
			? new ResolvedFunctionGroupExpressionNode(new FunctionGroupType("fun", [], "fun"), node)
			: ResolveLambda(node, target?.Type, !isTrial || checksBody, true, open: target?.Open ?? []);
	}
	
	private FunctionType? ExpectLambda(CallShape shape, IReadOnlyList<IResolvedExpressionNode?> values, int index,
		LambdaAnnotations annotations)
	{
		if (index >= shape.Parameters.Count || shape.Modes[index] == ParameterMode.Mut ||
		    GetExpectedFunction(shape.Parameters[index], shape) is not FunctionType parameter)
			return null;
		
		if (!TypePool.FindTypeParameters(parameter).Any(shape.Open.Contains))
			return parameter;
		
		var inputs = CreateInferenceInputs(shape, values).Concat(CreateAnnotationInputs(parameter, annotations));
		var known = _inference.InferKnown(shape.Open, inputs, shape.ReturnType, ExpectedType, fixLiterals: true);
		var map = shape.Open
			.Select((type, i) => (Parameter: type, Type: known[i]))
			.Where(static entry => entry.Type is not null)
			.ToDictionary(static entry => entry.Parameter, static entry => entry.Type!);
		
		return _typePool.Substitute(parameter, map) as FunctionType;
	}
	
	private TypeSymbol GetExpectedFunction(TypeSymbol parameter, CallShape shape) => parameter switch
	{
		BorrowType { IsMutable: false } borrow => borrow.Target,
		TypeParameterSymbol bounded when FindFunctionBound(bounded, shape) is { } bound =>
			_typePool.GetRefFunctionType(bound),
		_ => parameter
	};
	
	private FunctionType? FindFunctionBound(TypeParameterSymbol parameter, CallShape shape) =>
		_typePool.GetFunctionBounds(parameter) is [var bound]
			? bound
			: _typePool.GetParameterBounds(parameter)
				.Select(trait => shape.Outer.GetValueOrDefault(trait))
				.OfType<FunctionType>()
				.FirstOrDefault();
	
	private LambdaAnnotations ResolveAnnotations(LambdaExpressionNode node)
	{
		var checkpoint = OpenCheckpoint();
		var context = CurrentResolutionContext;
		var annotations = new LambdaAnnotations(
			[..node.Parameters.Select(parameter => parameter.Type is { } type ? context.ResolveType(type) : null)],
			[
				..node.Parameters.Select(static parameter =>
					parameter.Mode is { } mode ? SymbolCollector.GetMode(mode) : (ParameterMode?)null)
			],
			node.ReturnType is { } returnType ? context.ResolveReturnType(returnType) : null);
		
		Rollback(checkpoint);
		return annotations;
	}
	
	private void ReportAnnotations(LambdaExpressionNode node)
	{
		var context = CurrentResolutionContext;
		foreach (var parameter in node.Parameters)
		{
			if (parameter.Type is { } type)
				context.ResolveType(type);
		}
		
		if (node.ReturnType is { } returnType)
			context.ResolveReturnType(returnType);
	}
	
	private IEnumerable<InferenceInput> CreateAnnotationInputs(FunctionType parameter, LambdaAnnotations annotations)
	{
		if (parameter.ParameterTypes.Length != annotations.Types.Length)
			yield break;
		
		for (var i = 0; i < annotations.Types.Length; i++)
		{
			if (annotations.Types[i] is not { } type || type is InvalidType)
				continue;
			
			var mode = annotations.Modes[i] ?? parameter.ParameterModes[i];
			yield return new(parameter.ParameterTypes[i], _typePool.GetPassedType(type, mode)) { IsExact = true };
		}
		
		if (annotations.ReturnType is { } returnType and not InvalidType)
			yield return new(parameter.ReturnType, returnType) { IsExact = true };
	}
	
	private sealed record LambdaTarget(FunctionType Type, ImmutableArray<TypeParameterSymbol> Open);
	
	private bool Fits(LambdaAnnotations annotations, FunctionType type,
		IReadOnlyCollection<TypeParameterSymbol> open) =>
		type.ParameterTypes.Length == annotations.Types.Length &&
		annotations.Types.Select((annotated, i) =>
				(annotations.Modes[i] ?? type.ParameterModes[i]) == type.ParameterModes[i] &&
				(annotated is null || !IsKnown(type.ParameterTypes[i], open) ||
				 _typePool.GetPassedType(annotated, type.ParameterModes[i]) == type.ParameterTypes[i]))
			.All(static fits => fits) &&
		(annotations.ReturnType is null || !IsKnown(type.ReturnType, open) ||
		 annotations.ReturnType == type.ReturnType);
	
	private IEnumerable<InferenceInput> CreateInferenceInputs(CallShape shape,
		IReadOnlyList<IResolvedExpressionNode?> values) => values
		.Take(shape.Parameters.Count)
		.Select((value, i) => value is null ? null : CreateInferenceInput(shape.Parameters[i], shape.Modes[i], value))
		.OfType<InferenceInput>();
	
	private bool IsDeferred(IExpressionNode node) => node switch
	{
		CallExpressionNode or ArrayExpressionNode or LambdaExpressionNode => true,
		MatchExpressionNode match => match.Arms.Any(arm => IsDeferred(arm.Value)),
		AccessExpressionNode => FindGenericCaseEnum(node) is not null,
		VarExpressionNode name => CurrentResolutionContext.Resolve(name.Identifier.Text) is null ||
		                          FindOpenLocal(name) is { } open && (open.IsCaseName || NeedsTarget(open.Initializer)),
		_ => false
	};
	
	private static IEnumerable<TypeSymbol> ParameterTypesAt(IEnumerable<CallShape> shapes, int index) => shapes
		.Where(shape => index < shape.Parameters.Count)
		.Select(shape => shape.Parameters[index]);
	
	private CallShape GetCallShape(ICallable candidate, TypeArgumentList? typeArguments)
	{
		FunctionInfo? info = candidate switch
		{
			FunctionCallable callable => callable.Info,
			ReceiverCallable callable => callable.Info,
			_ => null
		};
		
		var open = info is { } declared ? GetOpenTypeParameters(declared) : [];
		if (info is not { } function || open.IsEmpty || typeArguments is not { } explicitArguments ||
		    explicitArguments.Types.Length != open.Length)
			return CreateShape(candidate, open);
		
		var instantiated = InstantiateDeclared(function, explicitArguments.Types);
		return CreateShape(candidate is FunctionCallable
			? new FunctionCallable(instantiated, Repack(candidate, instantiated))
			: new ReceiverCallable(instantiated, instantiated.Signature.ReturnType,
				Repack(candidate, instantiated)), []);
	}
	
	private static CallShape CreateShape(ICallable candidate, ImmutableArray<TypeParameterSymbol> open) => new(
		candidate.ParameterTypes, open, candidate.ReturnType,
		[..candidate.ParameterTypes.Select((_, i) => candidate.GetMode(i))])
	{
		Outer = candidate switch
		{
			FunctionCallable callable => GetOuterArguments(callable.Info),
			ReceiverCallable callable => GetOuterArguments(callable.Info),
			_ => []
		}
	};
	
	private static Dictionary<TypeParameterSymbol, TypeSymbol> GetOuterArguments(FunctionInfo info)
	{
		var outer = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		if (info.TypeArguments.IsDefaultOrEmpty)
			return outer;
		
		var count = info.Symbol.TypeParameters.Length - info.Symbol.DeclaredTypeParameters.Length;
		for (var i = 0; i < count && i < info.TypeArguments.Length; i++)
			outer[info.Symbol.TypeParameters[i]] = info.TypeArguments[i];
		
		return outer;
	}
	
	private static CallShape CreateShape(ImmutableArray<TypeSymbol> parameters, NamedTypeSymbol definition) =>
		new(parameters, definition.TypeParameters, definition, [..parameters.Select(static _ => ParameterMode.Own)]);
	
	private bool AcceptsInferred(CallShape shape, IResolvedExpressionNode[] args)
	{
		if (args.Length != shape.Parameters.Count)
			return false;
		
		var result = _inference.Infer(shape.Open, CreateInferenceInputs(shape, args), shape.ReturnType, ExpectedType);
		if (!result.Succeeded ||
		    ReportConstraintViolation(shape.Open, result.Arguments, _ => SourceLocation.None) is not null)
			return false;
		
		var map = TypePool.CreateMap(shape.Open, result.Arguments);
		return args
			.Select((arg, i) => MatchArg(arg, _typePool.Substitute(shape.Parameters[i], map), shape.Modes[i],
				MaterializationMode.Overload, false).Cost)
			.All(static cost => cost != int.MaxValue);
	}
	
	private IResolvedExpressionNode VisitHinted(IExpressionNode node, TypeSymbol hint)
	{
		_expectations.Push(new(hint, true));
		try
		{
			var result = ((IExpressionNodeVisitor<IResolvedExpressionNode>)this).Visit(node);
			return result.Type == NativeSymbols.Void ? ReportVoidValue(result, null) : result;
		}
		finally
		{
			_expectations.Pop();
		}
	}
	
	private IResolvedExpressionNode VisitOperand(IExpressionNode node, IEnumerable<ICallable> candidates, int index,
		TypeSymbol? peer = null)
	{
		List<TypeSymbol> expected = [..ParameterTypesAt(candidates, index), ..peer is null ? [] : new[] { peer }];
		if (VisitCaseName(node, expected) is { } caseName)
			return caseName;
		
		return FindGenericCaseEnum(node) is { } definition && expected.OfType<EnumSymbol>()
			.Where(type => type.OriginalDefinition == definition)
			.Distinct()
			.ToList() is [var target]
			? VisitNode(node, target)
			: VisitNode(node, null);
	}
	
	private IResolvedExpressionNode VisitPeerOperand(IExpressionNode node, Func<IResolvedExpressionNode> visit,
		TypeSymbol peer)
	{
		if (node is not CallExpressionNode || peer is UntypedType or InvalidType or NeverType)
			return visit();
		
		var trial = OpenCheckpoint();
		var value = visit();
		if (value is not ResolvedInvalidExpressionNode { NeedsTargetType: true })
		{
			Commit(trial);
			return value;
		}
		
		Rollback(trial);
		var retry = OpenCheckpoint();
		var hinted = VisitHinted(node, peer);
		if (!IsInvalid(hinted))
		{
			Commit(retry);
			return hinted;
		}
		
		Rollback(retry);
		return visit();
	}
}