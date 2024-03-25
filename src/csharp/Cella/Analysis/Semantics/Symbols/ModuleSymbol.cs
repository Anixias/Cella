using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics.Symbols;

public sealed class ModuleSymbol : ISymbol
{
	public string Name { get; }
	public List<SourceLocation> DeclarationLocations { get; } = new();
	public List<SourceLocation> UsageLocations { get; } = new();
	public Scope Scope { get; }
	
	public ModuleSymbol(string name, Scope scope)
	{
		Name = name;
		Scope = scope;
	}
}