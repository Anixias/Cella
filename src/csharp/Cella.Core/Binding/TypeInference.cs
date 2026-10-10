using System.Collections.Immutable;
using Cella.Core.Collections;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public readonly record struct InferenceInput(TypeSymbol Parameter, TypeSymbol Argument)
{
	public bool IsExact { get; init; }
	public UntypedType? Literal { get; init; }
}

public sealed class InferenceResult
{
	public ImmutableArray<TypeSymbol> Arguments { get; init; } = [];
	public ImmutableArray<TypeParameterSymbol> Missing { get; init; } = [];
	public TypeParameterSymbol? Conflicted { get; init; }
	public ImmutableArray<TypeSymbol> Candidates { get; init; } = [];
	public bool Succeeded => Missing.IsEmpty && Conflicted is null;
}

public sealed class TypeInference(TypePool typePool)
{
	public InferenceResult Infer(ImmutableArray<TypeParameterSymbol> parameters, IEnumerable<InferenceInput> inputs,
		TypeSymbol? returnType, TypeSymbol? target)
	{
		var bounds = Collect(parameters, inputs);
		var inferred = new TypeSymbol?[parameters.Length];
		for (var i = 0; i < parameters.Length; i++)
		{
			if (Fix(bounds.Exact[i], bounds.Lower[i], out var conflict) is { } type)
				inferred[i] = type;
			else if (!conflict.IsEmpty)
				return new() { Conflicted = parameters[i], Candidates = conflict };
		}
		
		FillFromTarget(parameters, inferred, null, returnType, target);
		InferFromBounds(parameters, inferred);
		
		for (var i = 0; i < parameters.Length; i++)
		{
			if (inferred[i] is not null || bounds.Literals[i].Count == 0)
				continue;
			
			if (FixLiterals(bounds.Literals[i], out var conflict) is not { } type)
				return new() { Conflicted = parameters[i], Candidates = conflict };
			
			inferred[i] = type;
		}
		
		var missing = parameters.Where((_, i) => inferred[i] is null).ToImmutableArray();
		return missing.IsEmpty
			? new() { Arguments = [..inferred.Select(static type => type!)] }
			: new() { Missing = missing };
	}
	
	public ImmutableArray<TypeSymbol?> InferKnown(ImmutableArray<TypeParameterSymbol> parameters,
		IEnumerable<InferenceInput> inputs, TypeSymbol? returnType, TypeSymbol? target,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? chosen = null, bool fixLiterals = false)
	{
		var bounds = Collect(parameters, inputs);
		var inferred = new TypeSymbol?[parameters.Length];
		var conflicted = new bool[parameters.Length];
		for (var i = 0; i < parameters.Length; i++)
		{
			if (chosen?.GetValueOrDefault(parameters[i]) is { } type)
			{
				inferred[i] = type;
				continue;
			}
			
			inferred[i] = Fix(bounds.Exact[i], bounds.Lower[i], out var conflict);
			conflicted[i] = !conflict.IsEmpty;
		}
		
		FillFromTarget(parameters, inferred, conflicted, returnType, target);
		InferFromBounds(parameters, inferred);
		for (var i = 0; fixLiterals && i < parameters.Length; i++)
		{
			if (inferred[i] is null && bounds.Literals[i].Count > 0)
				inferred[i] = FixLiterals(bounds.Literals[i], out _);
		}
		
		return [..inferred.Select((type, i) => conflicted[i] ? null : type)];
	}
	
	private Bounds Collect(ImmutableArray<TypeParameterSymbol> parameters, IEnumerable<InferenceInput> inputs)
	{
		var bounds = new Bounds(parameters, typePool);
		foreach (var input in inputs)
		{
			if (input.Literal is { } literal)
				bounds.AddLiteral(input.Parameter, input.Argument, literal);
			else
				bounds.Unify(input.Parameter, input.Argument, input.IsExact);
		}
		
		return bounds;
	}
	
	private void FillFromTarget(ImmutableArray<TypeParameterSymbol> parameters, TypeSymbol?[] inferred, bool[]? skipped,
		TypeSymbol? returnType, TypeSymbol? target)
	{
		if (returnType is null || target is null || inferred.All(static type => type is not null))
			return;
		
		var expected = new Bounds(parameters, typePool);
		expected.Unify(returnType, target, false);
		for (var i = 0; i < parameters.Length; i++)
		{
			if (skipped?[i] != true)
				inferred[i] ??= Fix(expected.Exact[i], expected.Lower[i], out _);
		}
	}
	
	private void InferFromBounds(ImmutableArray<TypeParameterSymbol> parameters, TypeSymbol?[] inferred)
	{
		var progress = true;
		while (progress && inferred.Any(static type => type is null))
		{
			progress = false;
			for (var i = 0; i < parameters.Length; i++)
			{
				if (inferred[i] is not { } type || type is InvalidType)
					continue;
				
				foreach (var bound in typePool.GetBounds(parameters[i]))
				{
					var known = parameters
						.Select((parameter, j) => (Parameter: parameter, Type: inferred[j]))
						.Where(static entry => entry.Type is not null)
						.ToDictionary(static entry => entry.Parameter, static entry => entry.Type!);
					
					var expected = typePool.SubstituteTrait(bound, known);
					var open = new OrderedSet<TypeParameterSymbol>(parameters
						.Where((parameter, j) => inferred[j] is null &&
						                         expected.Arguments.Any(argument =>
							                         TypePool.FindTypeParameters(argument).Contains(parameter))));
					
					if (open.Count == 0)
						continue;
					
					var matches = FindTraitArguments(type, bound.Trait)
						.Select(arguments => Match(expected.Arguments, arguments, open))
						.OfType<Dictionary<TypeParameterSymbol, TypeSymbol>>()
						.ToList();
					
					if (matches is not [var match])
						continue;
					
					for (var j = 0; j < parameters.Length; j++)
					{
						if (inferred[j] is not null || !match.TryGetValue(parameters[j], out var found))
							continue;
						
						inferred[j] = found;
						progress = true;
					}
				}
			}
		}
	}
	
	private IEnumerable<ImmutableArray<TypeSymbol>> FindTraitArguments(TypeSymbol type, TraitSymbol trait) =>
		type is TypeParameterSymbol parameter
			? typePool.GetBounds(parameter)
				.Where(bound => bound.Trait == trait)
				.Select(static bound => bound.Arguments)
			: typePool.FindConformances(type)
				.Where(conformance => conformance.Trait == trait)
				.Select(conformance => typePool.GetConformanceArguments(conformance, type));
	
	private Dictionary<TypeParameterSymbol, TypeSymbol>? Match(ImmutableArray<TypeSymbol> expected,
		ImmutableArray<TypeSymbol> arguments, OrderedSet<TypeParameterSymbol> open)
	{
		var bindings = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		return expected.Length == arguments.Length &&
		       expected.Zip(arguments).All(pair => typePool.TryUnify(pair.First, pair.Second, open, bindings))
			? bindings
			: null;
	}
	
	private TypeSymbol? Fix(List<TypeSymbol> exact, List<TypeSymbol> lower, out ImmutableArray<TypeSymbol> conflict)
	{
		conflict = [];
		var exactTypes = exact.Distinct().ToList();
		var lowerTypes = lower.Distinct().ToList();
		switch (exactTypes)
		{
			case [_, _, ..]:
				conflict = [..exactTypes];
				return null;
			
			case [var only]:
				var mismatched = lowerTypes.Where(type => !ConvertsTo(type, only)).ToList();
				if (mismatched.Count == 0)
					return only;
				
				conflict = [only, ..mismatched];
				return null;
		}
		
		if (lowerTypes.Count == 0)
			return null;
		
		if (lowerTypes.Where(candidate => lowerTypes.All(type => ConvertsTo(type, candidate))).ToList() is [var best])
			return best;
		
		conflict = [..lowerTypes];
		return null;
	}
	
	private TypeSymbol? FixLiterals(List<(TypeSymbol Default, UntypedType Literal)> literals,
		out ImmutableArray<TypeSymbol> conflict)
	{
		conflict = [];
		if (literals.All(static literal => literal.Literal is UntypedIntegerType))
			return Fix([], [..literals.Select(static literal => literal.Default)], out conflict);
		
		if (literals.All(static literal => literal.Literal is UntypedIntegerType or UntypedFloatType))
			return NativeSymbols.Float64;
		
		var defaults = literals.Select(static literal => literal.Default).Distinct().ToList();
		if (defaults is [var only])
			return only;
		
		conflict = [..defaults];
		return null;
	}
	
	private bool ConvertsTo(TypeSymbol from, TypeSymbol to) =>
		from == to || typePool.ConversionTable.FindImplicit(from, to) is not null;
	
	private sealed class Bounds(ImmutableArray<TypeParameterSymbol> parameters, TypePool typePool)
	{
		public List<TypeSymbol>[] Exact { get; } = [..parameters.Select(static _ => new List<TypeSymbol>())];
		public List<TypeSymbol>[] Lower { get; } = [..parameters.Select(static _ => new List<TypeSymbol>())];
		
		public List<(TypeSymbol Default, UntypedType Literal)>[] Literals { get; } =
			[..parameters.Select(static _ => new List<(TypeSymbol, UntypedType)>())];
		
		public void AddLiteral(TypeSymbol parameter, TypeSymbol defaultType, UntypedType literal)
		{
			if (parameter is BorrowType { IsMutable: false } borrow)
				parameter = borrow.Target;
			
			if (IndexOf(parameter) is var index and >= 0)
				Literals[index].Add((defaultType, literal));
		}
		
		public void Unify(TypeSymbol parameter, TypeSymbol argument, bool exact)
		{
			if (IndexOf(parameter) is var index and >= 0)
			{
				(exact ? Exact : Lower)[index].Add(argument);
				return;
			}
			
			if (!Mentions(parameter))
				return;
			
			switch (parameter, argument)
			{
				case (BorrowType p, BorrowType a):
					Unify(p.Target, a.Target, true);
					break;
				
				case (BorrowType { IsMutable: false } p, _):
					Unify(p.Target, argument, exact);
					break;
				
				case (PointerType p, PointerType a):
					Unify(p.BaseType, a.BaseType, true);
					break;
				
				case (DynType { Parameter: { } p }, DynType { Instance: { } a }):
					Unify(p, a, true);
					break;
				
				case (DynType { Parameter: { } p }, DynType { Parameter: { } a }):
					Unify(p, a, true);
					break;
				
				case (DynType { Parameter: { } p }, DynType { Function: { } a }):
					Unify(p, a, true);
					break;
				
				case (DynType { Instance: { } p }, DynType { Instance: { } a }):
					Unify(p, a, true);
					break;
				
				case (TraitType p, TraitType a) when p.Trait == a.Trait:
					for (var i = 0; i < p.Arguments.Length; i++)
						Unify(p.Arguments[i], a.Arguments[i], true);
					
					break;
				
				case (FStrType p, FStrType a):
					Unify(p.Value, a.Value, true);
					break;
				
				case (ArrayType p, ArrayType a):
					Unify(p.ElementType, a.ElementType, true);
					break;
				
				case (NamedTypeSymbol p, NamedTypeSymbol a) when p.Definition == a.Definition:
					for (var i = 0; i < p.TypeArguments.Length; i++)
						Unify(p.TypeArguments[i], a.TypeArguments[i], true);
					
					break;
				
				case (FunctionType, ClosureType or TypeParameterSymbol or DynType) when
					typePool.GetCallableSignatures(argument) is [var signature]:
					Unify(parameter, signature, exact);
					break;
				
				case (FunctionType p, FunctionType a) when p.ParameterTypes.Length == a.ParameterTypes.Length:
					for (var i = 0; i < p.ParameterTypes.Length; i++)
						Unify(p.ParameterTypes[i], GetParameterArgument(p, i, a.ParameterTypes[i]), true);
					
					Unify(p.ReturnType, a.ReturnType, true);
					break;
				
				case (_, BorrowType a):
					Unify(parameter, a.Target, exact);
					break;
			}
		}
		
		private TypeSymbol GetParameterArgument(FunctionType parameter, int index, TypeSymbol argument) =>
			parameter.ParameterModes[index] == ParameterMode.ReadOnly &&
			parameter.ParameterTypes[index] is TypeParameterSymbol &&
			argument is FunctionType { IsRef: true, IsExternal: false } function
				? typePool.GetPlainFunctionType(function)
				: argument;
		
		private int IndexOf(TypeSymbol type) =>
			type is TypeParameterSymbol parameter ? parameters.IndexOf(parameter) : -1;
		
		private bool Mentions(TypeSymbol type) => type switch
		{
			TypeParameterSymbol parameter => parameters.Contains(parameter),
			DynType { Parameter: { } parameter } => parameters.Contains(parameter),
			DynType { Instance: { } trait } => Mentions(trait),
			DynType { Function: { } function } => Mentions(function),
			TraitType trait => trait.Arguments.Any(Mentions),
			FStrType fstr => Mentions(fstr.Value),
			NamedTypeSymbol named => named.TypeArguments.Any(Mentions),
			PointerType pointer => Mentions(pointer.BaseType),
			BorrowType borrow => Mentions(borrow.Target),
			ArrayType array => Mentions(array.ElementType),
			FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(Mentions),
			_ => false
		};
	}
}