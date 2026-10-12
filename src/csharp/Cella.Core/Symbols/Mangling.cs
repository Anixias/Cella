using System.Text;
using Cella.Core.Binding;

namespace Cella.Core.Symbols;

public static class Mangling
{
	public static string Mangle(Symbol symbol, FunctionSignature signature, ModuleIndex? modules,
		params IEnumerable<string> qualifierParts)
	{
		var sb = new StringBuilder(Mangle(symbol, modules, qualifierParts));
		if (symbol is FunctionSymbol { IsConversion: true })
			sb.Append("->").Append(MangleType(signature.ReturnType, modules));
		
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
		new StringBuilder().Append('?').Append(QualifyAssembly(modules?.AssemblyName))
			.AppendJoin('.', qualifierParts.Append(MangleName(symbol, modules))).ToString();
	
	public static string MangleStaticRegistry(string assemblyName) => $"?{QualifyAssembly(assemblyName)}.statics";
	
	private static string QualifyAssembly(string? assemblyName) =>
		assemblyName is null ? "" : $"{EscapeExportSyntax(assemblyName)}!";
	
	public static string MangleName(Symbol symbol, ModuleIndex? modules)
	{
		var name = symbol switch
		{
			FunctionSymbol function => function.DeclaredName,
			ImplSymbol impl => impl.TraitNames,
			_ => symbol.Name
		};
		
		return EscapeExportSyntax(modules?.FindPrivateFile(symbol) is { } file ? $"{name}@{file}" : name);
	}
	
	private static string EscapeExportSyntax(string name) => name.Replace('=', '$').Replace(',', ';');
	
	public static string MangleInstantiation(string definitionName, FunctionSignature signature,
		IEnumerable<TypeSymbol> typeArguments, ModuleIndex? modules)
	{
		var parameters = definitionName.IndexOf(':');
		var sb = new StringBuilder(MangleInstantiation(parameters < 0 ? definitionName : definitionName[..parameters],
			typeArguments, modules));
		
		if (signature.ParameterTypes.Length > 0)
			sb.Append(':').AppendJoin('.',
				signature.ParameterTypes.Select((_, i) => MangleParameter(signature, i, modules)));
		
		return sb.ToString();
	}
	
	public static string MangleInstantiation(string definitionName, IEnumerable<TypeSymbol> typeArguments,
		ModuleIndex? modules) =>
		$"{definitionName}[{string.Join(';', typeArguments.Select(argument => MangleType(argument, modules)))}]";
	
	public static string MangleTypeName(TypeSymbol type, ModuleIndex? modules) => MangleType(type, modules);
	
	private static string MangleType(TypeSymbol type, ModuleIndex? modules) => type switch
	{
		NamedTypeSymbol { IsGenericInstance: true } instance =>
			$"{MangleType(instance.Definition, modules)}[{string.Join(';',
				instance.TypeArguments.Select(argument => MangleType(argument, modules)))}]",
		PointerType { BaseType: var baseType } when baseType != NativeSymbols.Void =>
			$"ptr[{MangleType(baseType, modules)}]",
		BorrowType borrow => $"{(borrow.IsMutable ? "mut" : "imm")}[{MangleType(borrow.Target, modules)}]",
		DynType { Instance: { } trait } => $"dyn[{MangleTrait(trait, modules)}]",
		DynType { Function: { } function } => $"dyn[{MangleFunctionType(function, modules)}]",
		FStrType { Value.Instance: { } trait } => $"fstr[{MangleTrait(trait, modules)}]",
		TraitType trait => MangleTrait(trait, modules),
		ArrayType { LengthParameter: { } length } array =>
			$"array[{MangleType(array.ElementType, modules)}, {length.Name}]",
		ValueArgumentType value => value.Value.ToString(),
		ArrayType array => $"array[{MangleType(array.ElementType, modules)}, {array.Length}]",
		FunctionType function => MangleFunctionType(function, modules),
		ClosureType { Function.TypeArguments.IsDefaultOrEmpty: true } closure => $"own[{closure.DefinitionName}]",
		ClosureType closure =>
			$"own[{MangleInstantiation(closure.DefinitionName, closure.Function.TypeArguments, modules)}]",
		_ when modules?.FindModule(type) is { } module =>
			$"{QualifyAssembly(modules.FindAssembly(type))}{module.ModuleName.Text}.{MangleName(type, modules)}",
		_ => type.Name
	};
	
	private static string MangleTrait(TraitType trait, ModuleIndex? modules)
	{
		var name = modules?.FindModule(trait.Trait) is { } module
			? $"{QualifyAssembly(modules.FindAssembly(trait.Trait))}{module.ModuleName.Text}.{MangleName(trait.Trait, modules)}"
			: trait.Trait.Name;
		
		return trait.Arguments.IsEmpty
			? name
			: $"{name}[{string.Join(';', trait.Arguments.Select(argument => MangleType(argument, modules)))}]";
	}
	
	private static string MangleFunctionType(FunctionType function, ModuleIndex? modules)
	{
		var parameters = string.Join(';', function.ParameterTypes.Select((type, i) => function.ParameterModes[i] switch
		{
			ParameterMode.Mut => $"mut {MangleType(function.GetDeclaredType(i), modules)}",
			ParameterMode.Own => $"own {MangleType(type, modules)}",
			_ => MangleType(type, modules)
		}));
		
		var returnType = MangleType(function.ReturnType, modules);
		var signature = function.ReturnType == NativeSymbols.Void ? parameters
			: parameters.Length == 0 ? $"-> {returnType}"
			: $"{parameters} -> {returnType}";
		
		return $"{(function.IsExternal ? "ext fun" : function.IsRef ? "ref fun" : "fun")}[{signature}]";
	}
}