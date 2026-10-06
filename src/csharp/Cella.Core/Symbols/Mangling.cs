using System.Text;
using Cella.Core.Binding;

namespace Cella.Core.Symbols;

public static class Mangling
{
	public static string Mangle(Symbol symbol, FunctionSignature signature, ModuleIndex? modules,
		params IEnumerable<string> qualifierParts)
	{
		var sb = new StringBuilder(Mangle(symbol, modules, qualifierParts));
		if (signature.ParameterTypes.Length > 0)
			sb.Append(':').AppendJoin('.',
				signature.ParameterTypes.Select((_, i) => MangleParameter(signature, i, modules)));
		
		return sb.ToString();
	}
	
	private static string MangleParameter(FunctionSignature signature, int index, ModuleIndex? modules) =>
		signature.GetMode(index) == ParameterMode.Mut
			? $"mut {MangleType(signature.GetDeclaredType(index), modules)}"
			: MangleType(signature.ParameterTypes[index], modules);
	
	public static string Mangle(Symbol symbol, ModuleIndex? modules, params IEnumerable<string> qualifierParts) =>
		new StringBuilder().Append('?').AppendJoin('.', qualifierParts.Append(MangleName(symbol, modules))).ToString();
	
	public static string MangleName(Symbol symbol, ModuleIndex? modules) =>
		modules?.FindPrivateFile(symbol) is { } file ? $"{symbol.Name}@{file}" : symbol.Name;
	
	private static string MangleType(TypeSymbol type, ModuleIndex? modules) => type switch
	{
		PointerType { BaseType: var baseType } when baseType != NativeSymbols.Void =>
			$"ptr[{MangleType(baseType, modules)}]",
		BorrowType borrow => $"{(borrow.IsMutable ? "mut" : "imm")}[{MangleType(borrow.Target, modules)}]",
		ArrayType { Length.Sign: < 0 } array => $"array[{MangleType(array.ElementType, modules)}]",
		ArrayType array => $"array[{MangleType(array.ElementType, modules)}, {array.Length}]",
		FunctionType function => MangleFunctionType(function, modules),
		_ when modules?.FindModule(type) is { } module => $"{module.ModuleName.Text}.{MangleName(type, modules)}",
		_ => type.Name
	};
	
	private static string MangleFunctionType(FunctionType function, ModuleIndex? modules)
	{
		var parameters = string.Join(", ", function.ParameterTypes.Select((type, i) => function.ParameterModes[i] switch
		{
			ParameterMode.Mut => $"mut {MangleType(function.GetDeclaredType(i), modules)}",
			ParameterMode.Own => $"own {MangleType(type, modules)}",
			_ => MangleType(type, modules)
		}));
		
		var returnType = MangleType(function.ReturnType, modules);
		var signature = function.ReturnType == NativeSymbols.Void ? parameters
			: parameters.Length == 0 ? $"-> {returnType}"
			: $"{parameters} -> {returnType}";
		
		return $"{(function.IsExternal ? "ext fun" : "fun")}[{signature}]";
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