using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class ImportEnvironment
(
	IEnumerable<Symbol> importedSymbols,
	IEnumerable<Symbol> explicitSymbols,
	IEnumerable<Symbol> hiddenSymbols,
	IEnumerable<ModuleSymbol> activatedModules
)
{
	private readonly ImmutableHashSet<ModuleSymbol> _activatedModules = activatedModules.ToImmutableHashSet();
	
	public bool Activates(ModuleSymbol module) => _activatedModules.Contains(module);
	
	public IEnumerable<Symbol> ImportedSymbols => _importedSymbols.SelectMany(static kvp => kvp.Value);
	
	private readonly ImmutableDictionary<string, ImmutableArray<Symbol>> _importedSymbols = importedSymbols
		.Distinct()
		.GroupBy(static s => s.Name)
		.ToImmutableDictionary(static g => g.Key, static g => g.ToImmutableArray());
	
	private readonly ImmutableHashSet<Symbol> _explicitSymbols = explicitSymbols.ToImmutableHashSet();
	private readonly ILookup<string, Symbol> _hiddenSymbols = hiddenSymbols.ToLookup(static s => s.Name);
	
	public ImmutableArray<Symbol> Resolve(string name) =>
		_importedSymbols.GetValueOrDefault(name, ImmutableArray<Symbol>.Empty);
	
	public bool IsExplicit(Symbol symbol) => _explicitSymbols.Contains(symbol);
	
	public IEnumerable<Symbol> ResolveHidden(string name) => _hiddenSymbols[name];
}