using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics.Symbols;

public sealed class ParameterSymbol : ISymbol
{
	public string Name { get; }
	public List<SourceLocation> DeclarationLocations { get; } = new();
	public List<SourceLocation> UsageLocations { get; } = new();

	public ParameterSymbol(string name, Scope scope)
	{
		Name = name;
	}
}