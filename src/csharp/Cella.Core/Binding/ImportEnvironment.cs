using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class ImportUse(ImportExpression import, Token? name)
{
	public ImportExpression Import { get; } = import;
	public Token? Name { get; } = name;
}

public sealed class ImportEnvironment
(
	IEnumerable<Symbol> importedSymbols,
	IEnumerable<Symbol> explicitSymbols,
	IEnumerable<Symbol> hiddenSymbols,
	IEnumerable<ModuleSymbol> activatedModules,
	IEnumerable<(Symbol Symbol, ImportUse Use)> sources,
	IEnumerable<(ModuleSymbol Module, ImportUse Use)> moduleSources
)
{
	private readonly ImmutableHashSet<ModuleSymbol> _activatedModules = activatedModules.ToImmutableHashSet();
	private readonly ILookup<Symbol, ImportUse> _sources = sources.ToLookup(static s => s.Symbol, static s => s.Use);
	
	private readonly ILookup<ModuleSymbol, ImportUse> _moduleSources =
		moduleSources.ToLookup(static s => s.Module, static s => s.Use);
	
	private readonly HashSet<ImportUse> _used = [];
	
	public bool Activates(ModuleSymbol module)
	{
		if (!_activatedModules.Contains(module))
			return false;
		
		MarkUsed(_moduleSources[module]);
		return true;
	}
	
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
	
	public IEnumerable<Symbol> ResolveHidden(string name)
	{
		foreach (var symbol in _hiddenSymbols[name])
			MarkResolved(symbol);
		
		return _hiddenSymbols[name];
	}
	
	public void MarkResolved(Symbol symbol) => MarkUsed(_sources[symbol]);
	
	public IEnumerable<ImportUse> SourcesOf(Symbol symbol) => _sources[symbol];
	
	public IEnumerable<ImportUse> FindUnused()
	{
		lock (_used)
			return _sources.SelectMany(static group => group)
				.Concat(_moduleSources.SelectMany(static group => group))
				.Distinct()
				.Where(use => !_used.Contains(use))
				.ToList();
	}
	
	private void MarkUsed(IEnumerable<ImportUse> uses)
	{
		lock (_used)
			_used.UnionWith(uses);
	}
}