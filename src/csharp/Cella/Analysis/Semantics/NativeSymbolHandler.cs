using Cella.Analysis.Semantics.Symbols;

namespace Cella.Analysis.Semantics;

public static class NativeSymbolHandler
{
	public static readonly TypeSymbol Int64 = new(nameof(Int64), new Scope());

	static NativeSymbolHandler()
	{
		// Todo: Populate native symbol tables
	}
	
	public static Scope CreateGlobalScope()
	{
		var globalScope = new Scope();
		
		globalScope.AddSymbol(Int64);
		
		return globalScope;
	}
}