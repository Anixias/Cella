using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed record TypeConstantUse(TypeSymbol Owner, TraitType? Trait, GlobalSymbol Constant);

public sealed partial class TypePool
{
	private readonly Dictionary<FunctionSymbol, List<TypeConstantUse>> _typeConstantUses = [];
	
	public TypeConstantUse Substitute(TypeConstantUse use, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) =>
		use with
		{
			Owner = Substitute(use.Owner, map),
			Trait = use.Trait is { } trait ? SubstituteTrait(trait, map) : null
		};
	
	public static bool ContainsTypeParameters(TypeConstantUse use) => ContainsTypeParameters(use.Owner) ||
	                                                                  use.Trait?.Arguments.Any(
		                                                                  ContainsTypeParameters) ==
	                                                                  true;
	
	public Dictionary<TypeParameterSymbol, TypeSymbol> GetTypeConstantMap(TypeConstantUse use)
	{
		if (use.Trait is { } trait)
			return CreateTraitMap(trait, use.Owner);
		
		return use.Constant.Impl is { } impl ? GetMemberBlockMap(impl, use.Owner) : [];
	}
	
	public Dictionary<TypeParameterSymbol, TypeSymbol> GetMemberBlockMap(ImplSymbol impl, TypeSymbol type)
	{
		if (GetMemberBlockTarget(impl) is TypeParameterSymbol target)
			return new() { [target] = type };
		
		var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		var arguments = GetShapeArguments(type);
		for (var i = 0; i < impl.TargetArguments.Length && i < arguments.Length; i++)
			if (impl.TargetArguments[i] is TypeParameterSymbol parameter)
				map[parameter] = arguments[i];
		
		return map;
	}
	
	public IReadOnlyList<TypeConstantUse> GetTypeConstantUses(FunctionSymbol function) =>
		_typeConstantUses.GetValueOrDefault(function) ?? [];
	
	public bool AddTypeConstantUse(FunctionSymbol function, TypeConstantUse use)
	{
		if (!_typeConstantUses.TryGetValue(function, out var list))
			_typeConstantUses[function] = list = [];
		
		if (list.Contains(use))
			return false;
		
		list.Add(use);
		return true;
	}
	
	public void RemoveTypeConstantUse(FunctionSymbol function, TypeConstantUse use) =>
		_typeConstantUses.GetValueOrDefault(function)?.Remove(use);
}