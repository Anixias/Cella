using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	private readonly Dictionary<TypeParameterSymbol, List<TraitType>> _constantBounds = [];
	
	public void AddConstantBound(TypeParameterSymbol parameter, TraitType trait) =>
		_constantBounds.GetOrAdd(parameter).Add(trait);
	
	public bool HasConstantBound(TypeParameterSymbol parameter, TraitSymbol trait) =>
		_constantBounds.TryGetValue(parameter, out var bounds) &&
		bounds.Any(bound => Requires(bound, trait, parameter));
	
	public bool Requires(TraitType owner, TraitSymbol trait, TypeSymbol self)
	{
		var visited = new HashSet<TraitType>();
		var pending = new Queue<TraitType>([owner]);
		while (pending.TryDequeue(out var current))
		{
			if (!visited.Add(current))
				continue;
			
			if (current.Trait == trait)
				return true;
			
			foreach (var required in GetRequiredTraits(current, self))
				pending.Enqueue(required);
		}
		
		return false;
	}
	
	public bool IsConstantConformance(TypeSymbol type, TraitType trait) => type is TypeParameterSymbol parameter
		? HasConstantBound(parameter, trait.Trait)
		: FindConformance(type, trait) is { IsConstant: true };
	
	private TraitType? FindUnmetConstantTrait(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		if (!_constantBounds.TryGetValue(parameter, out var bounds))
			return null;
		
		var substitution = map?.ToDictionary() ?? [];
		substitution[parameter] = argument;
		return bounds
			.Select(bound => SubstituteTrait(bound, substitution))
			.FirstOrDefault(bound => Conforms(argument, bound) && !IsConstantConformance(argument, bound));
	}
}