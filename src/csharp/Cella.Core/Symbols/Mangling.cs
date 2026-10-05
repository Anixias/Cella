using System.Text;

namespace Cella.Core.Symbols;

public static class Mangling
{
	public static string Mangle(Symbol symbol, FunctionSignature signature,
		params IEnumerable<string> qualifierParts)
	{
		var sb = new StringBuilder()
			.Append('?')
			.AppendJoin('.', qualifierParts.Append(symbol.Name));
		
		if (signature.ParameterTypes.Length > 0)
			sb.Append(':').AppendJoin('.', signature.ParameterTypes.Select((_, i) => MangleParameter(signature, i)));
		
		return sb.ToString();
	}
	
	private static string MangleParameter(FunctionSignature signature, int index) =>
		signature.GetMode(index) == ParameterMode.Mut
			? $"mut {Mangle(signature.GetDeclaredType(index))}"
			: Mangle(signature.ParameterTypes[index]);
	
	public static string Mangle(Symbol symbol, params IEnumerable<string> qualifierParts) =>
		new StringBuilder().Append('?').AppendJoin('.', qualifierParts.Append(symbol.Name)).ToString();
	
	public static string Mangle(TypeSymbol type)
	{
		// TODO Handle complex types
		return type.Name;
	}
	
	public static string Demangle(ReadOnlySpan<char> name)
	{
		var nameStart = name.IndexOf('?');
		var nameEnd = name.IndexOf(':');
		
		if (nameStart < 0)
			return new(name);
		
		if (nameEnd < 0)
			nameEnd = name.Length;
		
		return name[(nameStart + 1)..nameEnd].ToString();
	}
}