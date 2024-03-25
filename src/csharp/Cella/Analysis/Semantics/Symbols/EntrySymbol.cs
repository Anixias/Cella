using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics.Symbols;

public sealed class EntrySymbol : ISymbol
{
	public string Name { get; }
	public List<SourceLocation> DeclarationLocations { get; } = new();
	public List<SourceLocation> UsageLocations { get; } = new();
	public Scope Scope { get; }
	public ImmutableArray<ParameterSymbol> Parameters { get; }
	
	public EntrySymbol(string name, Scope scope, IEnumerable<ParameterSymbol> parameters)
	{
		Name = name;
		Scope = scope;
		Parameters = parameters.ToImmutableArray();
	}
}