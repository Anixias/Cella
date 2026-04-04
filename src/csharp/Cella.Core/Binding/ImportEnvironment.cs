using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class ImportEnvironment(IEnumerable<Symbol> importedSymbols)
{
	private readonly ImmutableDictionary<string, ImmutableArray<Symbol>> _importedSymbols = importedSymbols
		.GroupBy(static s => s.Name)
		.ToImmutableDictionary(static g => g.Key, static g => g.ToImmutableArray());
	
	public ImmutableArray<Symbol> Resolve(string name) =>
		_importedSymbols.GetValueOrDefault(name, ImmutableArray<Symbol>.Empty);
}

// TODO File-specific symbol table for all imported symbols
// It would automatically import all symbols in its module
// - for the module in this assembly, all non-file-private symbols
// - for the module in other assemblies, all public symbols
// Then, import all symbols in import expressions
// - for import expressions whose module is in this assembly, all non-file-private symbols referenced (error if not found)
// - for other assemblies, all public symbols referenced (error if not found)
// ^ all of these symbols are grouped by name; ambiguous if a name has more than 1 match