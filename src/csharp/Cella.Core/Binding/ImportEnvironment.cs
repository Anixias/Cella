using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class ImportEnvironment(IEnumerable<Symbol> importedSymbols, IEnumerable<Symbol> explicitSymbols)
{
	public IEnumerable<Symbol> ImportedSymbols => _importedSymbols.SelectMany(static kvp => kvp.Value);
	
	private readonly ImmutableDictionary<string, ImmutableArray<Symbol>> _importedSymbols = importedSymbols
		.Distinct()
		.GroupBy(static s => s.Name)
		.ToImmutableDictionary(static g => g.Key, static g => g.ToImmutableArray());
	
	private readonly ImmutableHashSet<Symbol> _explicitSymbols = explicitSymbols.ToImmutableHashSet();
	
	public ImmutableArray<Symbol> Resolve(string name) =>
		_importedSymbols.GetValueOrDefault(name, ImmutableArray<Symbol>.Empty);
	
	public bool IsExplicit(Symbol symbol) => _explicitSymbols.Contains(symbol);
}

// TODO File-specific symbol table for all imported symbols
// It would automatically import all symbols in its module
// - for the module in this assembly, all non-file-private symbols
// - for the module in other assemblies, all public symbols
// Then, import all symbols in import expressions
// - for import expressions whose module is in this assembly, all non-file-private symbols referenced
// - for other assemblies, all public symbols referenced
// ^ all of these symbols are grouped by name; ambiguous if a name has more than 1 match