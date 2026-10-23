using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public enum GenericArgumentKind
{
	Type,
	Value,
	Trait
}

public static class TypeOverloads
{
	public static ImmutableArray<TypeParameterSymbol> GetParameters(Symbol symbol) => symbol switch
	{
		NamedTypeSymbol named => named.TypeParameters,
		TraitSymbol trait => trait.TypeParameters,
		_ => []
	};
	
	public static GenericArgumentKind GetKind(TypeParameterSymbol parameter) =>
		parameter.IsValue ? GenericArgumentKind.Value
		: parameter.IsTrait ? GenericArgumentKind.Trait
		: GenericArgumentKind.Type;
	
	public static bool CanOverload(Symbol first, Symbol second) =>
		first is NamedTypeSymbol && second is NamedTypeSymbol ||
		first is TraitSymbol && second is TraitSymbol;
	
	public static bool Overlap(Symbol first, Symbol second)
	{
		var mine = GetParameters(first);
		var theirs = GetParameters(second);
		var lowest = Math.Max(TypePool.CountRequiredArguments(mine), TypePool.CountRequiredArguments(theirs));
		var highest = Math.Min(mine.Length, theirs.Length);
		for (var count = lowest; count <= highest; count++)
		{
			if (Enumerable.Range(0, count).All(i => GetKind(mine[i]) == GetKind(theirs[i])))
				return true;
		}
		
		return false;
	}
	
	public static bool HaveSameParameters(Symbol first, Symbol second)
	{
		var mine = GetParameters(first);
		var theirs = GetParameters(second);
		return mine.Length == theirs.Length &&
		       mine.Zip(theirs).All(static pair => GetKind(pair.First) == GetKind(pair.Second) &&
		                                           pair.First.DeclaresDefault == pair.Second.DeclaresDefault);
	}
	
	public static IReadOnlyList<Symbol>? FindFamily(Symbol? symbol)
	{
		if (symbol is not AmbiguousSymbol { Candidates: [var first, _, ..] candidates } ||
		    !candidates.All(candidate => CanOverload(first, candidate)))
			return null;
		
		for (var i = 0; i < candidates.Length; i++)
		{
			for (var j = i + 1; j < candidates.Length; j++)
			{
				if (Overlap(candidates[i], candidates[j]))
					return null;
			}
		}
		
		return candidates;
	}
	
	public static Symbol? ChooseBare(Symbol? symbol) =>
		FindFamily(symbol) is { } family && family.FirstOrDefault(static member => Accepts(member, [])) is { } bare
			? bare
			: symbol;
	
	public static bool Accepts(Symbol member, IReadOnlyList<GenericArgumentKind?> kinds)
	{
		var parameters = GetParameters(member);
		return kinds.Count >= TypePool.CountRequiredArguments(parameters) && kinds.Count <= parameters.Length &&
		       kinds.Select((kind, i) => kind is null || kind == GetKind(parameters[i])).All(static fits => fits);
	}
	
	public static bool AcceptsCount(Symbol member, int count)
	{
		var parameters = GetParameters(member);
		return count >= TypePool.CountRequiredArguments(parameters) && count <= parameters.Length;
	}
}