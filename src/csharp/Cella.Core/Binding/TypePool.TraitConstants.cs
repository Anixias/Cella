using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed record TraitConstantUse(TypeSymbol Owner, GlobalSymbol Constant);

public sealed partial class TypePool
{
	private readonly Dictionary<FunctionSymbol, List<TraitConstantUse>> _traitConstantUses = [];
	
	public IReadOnlyList<TraitConstantUse> GetTraitConstantUses(FunctionSymbol function) =>
		_traitConstantUses.GetValueOrDefault(function) ?? [];
	
	public bool AddTraitConstantUse(FunctionSymbol function, TraitConstantUse use)
	{
		if (!_traitConstantUses.TryGetValue(function, out var list))
			_traitConstantUses[function] = list = [];
		
		if (list.Contains(use))
			return false;
		
		list.Add(use);
		return true;
	}
	
	public void RemoveTraitConstantUse(FunctionSymbol function, TraitConstantUse use) =>
		_traitConstantUses.GetValueOrDefault(function)?.Remove(use);
}