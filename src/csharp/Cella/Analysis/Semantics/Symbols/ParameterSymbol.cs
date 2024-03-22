namespace Cella.Analysis.Semantics.Symbols;

public sealed class ParameterSymbol : ISymbol
{
	public string SymbolTypeName => "a parameter";
	public string Name { get; }
	
	public ParameterSymbol(string name, Scope scope)
	{
		Name = name;
	}
}