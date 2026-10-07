using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
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

public sealed class TypeInference(ConversionTable conversions)
{
	public InferenceResult Infer(ImmutableArray<TypeParameterSymbol> parameters, IEnumerable<InferenceInput> inputs,
		TypeSymbol? returnType, TypeSymbol? target)
	{
		var bounds = new Bounds(parameters);
		foreach (var input in inputs)
		{
			if (input.Literal is { } literal)
				bounds.AddLiteral(input.Parameter, input.Argument, literal);
			else
				bounds.Unify(input.Parameter, input.Argument, input.IsExact);
		}
		
		var inferred = new TypeSymbol?[parameters.Length];
		for (var i = 0; i < parameters.Length; i++)
		{
			if (Fix(bounds.Exact[i], bounds.Lower[i], out var conflict) is { } type)
				inferred[i] = type;
			else if (!conflict.IsEmpty)
				return new() { Conflicted = parameters[i], Candidates = conflict };
		}
		
		if (returnType is not null && target is not null && inferred.Any(static type => type is null))
		{
			var expected = new Bounds(parameters);
			expected.Unify(returnType, target, false);
			for (var i = 0; i < parameters.Length; i++)
				inferred[i] ??= Fix(expected.Exact[i], expected.Lower[i], out _);
		}
		
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
		from == to || conversions.FindImplicit(from, to) is not null;
	
	private sealed class Bounds(ImmutableArray<TypeParameterSymbol> parameters)
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
				
				case (ArrayType p, ArrayType a):
					Unify(p.ElementType, a.ElementType, true);
					break;
				
				case (RecordSymbol p, RecordSymbol a) when p.Definition == a.Definition:
					for (var i = 0; i < p.TypeArguments.Length; i++)
						Unify(p.TypeArguments[i], a.TypeArguments[i], true);
					
					break;
				
				case (FunctionType p, FunctionType a) when p.ParameterTypes.Length == a.ParameterTypes.Length:
					for (var i = 0; i < p.ParameterTypes.Length; i++)
						Unify(p.ParameterTypes[i], a.ParameterTypes[i], true);
					
					Unify(p.ReturnType, a.ReturnType, true);
					break;
				
				case (_, BorrowType a):
					Unify(parameter, a.Target, exact);
					break;
			}
		}
		
		private int IndexOf(TypeSymbol type) =>
			type is TypeParameterSymbol parameter ? parameters.IndexOf(parameter) : -1;
		
		private bool Mentions(TypeSymbol type) => type switch
		{
			TypeParameterSymbol parameter => parameters.Contains(parameter),
			RecordSymbol record => record.TypeArguments.Any(Mentions),
			PointerType pointer => Mentions(pointer.BaseType),
			BorrowType borrow => Mentions(borrow.Target),
			ArrayType array => Mentions(array.ElementType),
			FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(Mentions),
			_ => false
		};
	}
}