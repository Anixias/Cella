using System.Collections.Immutable;

namespace Cella.Analysis.Semantics.Symbols;

public sealed class EntrySymbol : ISymbol
{
	public string SymbolTypeName => "an entry point";
	public string Name { get; }
	public Scope Scope { get; }
	public ImmutableArray<ParameterSymbol> Parameters { get; }
	
	public EntrySymbol(string name, Scope scope, IEnumerable<ParameterSymbol> parameters)
	{
		Name = name;
		Scope = scope;
		Parameters = parameters.ToImmutableArray();
	}
}