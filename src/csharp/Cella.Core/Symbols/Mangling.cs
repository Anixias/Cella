using System.Text;

namespace Cella.Core.Symbols;

public static class Mangling
{
	public static string Mangle(Symbol symbol, FunctionSignature signature, Func<Symbol, string?> findPrivateFile,
		params IEnumerable<string> qualifierParts)
	{
		var sb = new StringBuilder(Mangle(symbol, findPrivateFile, qualifierParts));
		if (signature.ParameterTypes.Length > 0)
			sb.Append(':').AppendJoin('.',
				signature.ParameterTypes.Select((_, i) => MangleParameter(signature, i, findPrivateFile)));
		
		return sb.ToString();
	}
	
	private static string MangleParameter(FunctionSignature signature, int index,
		Func<Symbol, string?> findPrivateFile) => signature.GetMode(index) == ParameterMode.Mut
		? $"mut {Mangle(signature.GetDeclaredType(index), findPrivateFile)}"
		: Mangle(signature.ParameterTypes[index], findPrivateFile);
	
	public static string Mangle(Symbol symbol, Func<Symbol, string?> findPrivateFile,
		params IEnumerable<string> qualifierParts) => new StringBuilder()
		.Append('?')
		.AppendJoin('.', qualifierParts.Append(Qualify(symbol.Name, symbol, findPrivateFile)))
		.ToString();
	
	public static string Mangle(TypeSymbol type, Func<Symbol, string?> findPrivateFile) => type switch
	{
		PointerType { BaseType: var baseType } when baseType != NativeSymbols.Void =>
			$"ptr[{Mangle(baseType, findPrivateFile)}]",
		BorrowType borrow => $"{(borrow.IsMutable ? "mut" : "imm")}[{Mangle(borrow.Target, findPrivateFile)}]",
		ArrayType { Length.Sign: < 0 } array => $"array[{Mangle(array.ElementType, findPrivateFile)}]",
		ArrayType array => $"array[{Mangle(array.ElementType, findPrivateFile)}, {array.Length}]",
		FunctionType function => MangleFunctionType(function, findPrivateFile),
		_ => Qualify(type.Name, type, findPrivateFile)
	};
	
	private static string MangleFunctionType(FunctionType function, Func<Symbol, string?> findPrivateFile)
	{
		var parameters = string.Join(", ", function.ParameterTypes.Select((type, i) => function.ParameterModes[i] switch
		{
			ParameterMode.Mut => $"mut {Mangle(function.GetDeclaredType(i), findPrivateFile)}",
			ParameterMode.Own => $"own {Mangle(type, findPrivateFile)}",
			_ => Mangle(type, findPrivateFile)
		}));
		
		var returnType = Mangle(function.ReturnType, findPrivateFile);
		var signature = function.ReturnType == NativeSymbols.Void ? parameters
			: parameters.Length == 0 ? $"-> {returnType}"
			: $"{parameters} -> {returnType}";
		
		return $"{(function.IsExternal ? "ext fun" : "fun")}[{signature}]";
	}
	
	private static string Qualify(string name, Symbol symbol, Func<Symbol, string?> findPrivateFile) =>
		findPrivateFile(symbol) is { } file ? $"{name}@{file}" : name;
	
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