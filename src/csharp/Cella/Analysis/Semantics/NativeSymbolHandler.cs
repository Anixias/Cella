using Cella.Analysis.Semantics.Symbols;

namespace Cella.Analysis.Semantics;

public static class NativeSymbolHandler
{
	public static readonly TypeSymbol Int32 = CreateNativeType(nameof(Int32));
	public static readonly TypeSymbol Int64 = CreateNativeType(nameof(Int64));

	private static TypeSymbol CreateNativeType(string name)
	{
		return new TypeSymbol(name, new Scope());
	}

	static NativeSymbolHandler()
	{
		// Todo: Populate native symbol tables
	}
	
	public static Scope CreateGlobalScope()
	{
		var globalScope = new Scope();
		
		globalScope.AddSymbol(Int32);
		globalScope.AddSymbol(Int64);
		
		return globalScope;
	}

	public static DataType? TypeOfValue(object? value)
	{
		return value switch
		{
			int => Int32.BaseType,
			long => Int64.BaseType,
			_ => null
		};
	}
}