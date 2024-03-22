namespace Cella.Analysis.Semantics.Symbols;

public sealed class ModuleSymbol : ISymbol
{
	public string SymbolTypeName => "a module";
	public string Name { get; }
	public Scope Scope { get; }
	
	public ModuleSymbol(string name, Scope scope)
	{
		Name = name;
		Scope = scope;
	}
}