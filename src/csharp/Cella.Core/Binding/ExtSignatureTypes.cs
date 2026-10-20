using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class ExtSignatureTypes(TypePool typePool)
{
	private readonly List<(TypeSymbol Type, ITypeNode Node, bool IsPassed, ErrorContainment.Owner Owner)> _types = [];
	
	public ErrorContainment? Containment { get; set; }
	
	public void Add(TypeSymbol type, ITypeNode node, bool isPassed) =>
		_types.Add((type, node, isPassed, Containment?.Current ?? default));
	
	public static bool PassesByValue(ParameterMode mode) => mode is ParameterMode.ReadOnly or ParameterMode.Own;
	
	public void Report(DiagnosticList diagnostics)
	{
		foreach (var (type, node, isPassed, owner) in _types)
		{
			Containment?.Enter(owner);
			Report(diagnostics, type, node, isPassed);
			Containment?.Exit();
		}
		
		_types.Clear();
	}
	
	private void Report(DiagnosticList diagnostics, TypeSymbol type, ITypeNode node, bool isPassed)
	{
		if (isPassed && type is ArrayType)
		{
			diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"Cannot use '{type.Name}' in 'ext' signatures"));
			
			return;
		}
		
		List<SourceLocation> nested = type is FunctionType { IsExternal: true } &&
		                              node is FunctionTypeNode { IsExternal: true }
			? [node.SourceLocation]
			: [..FindNestedExtFunctions(type, node)];
		
		bool IsOwn(SourceLocation location) => !nested.Any(inner => inner.Range.Contains(location.Range));
		
		if (HasPlainEnum(type) && Locate(type, node, HasPlainEnum) is var (enumType, _, enumLocation) &&
		    IsOwn(enumLocation))
			diagnostics.Add(Report(enumLocation, "Cannot use plain enums in 'ext' signatures", enumType,
				typePool.FindPlainEnum(enumType)!));
		
		if (IsForeign(type))
		{
			var (located, locatedNode, location) = Locate(type, node, IsForeign);
			if (IsOwn(location))
				diagnostics.Add(Report(location,
					$"Cannot use '{GetWrittenName(located, locatedNode)}' in 'ext' signatures", located,
					typePool.FindForeign(located)!));
		}
		else if (FindUnconstrained(type) is { } parameter)
		{
			var (_, _, location) = Locate(type, node, static part => FindUnconstrained(part) is not null);
			if (IsOwn(location))
				diagnostics.Add(new(DiagnosticSeverity.Error, location,
					$"Cannot use '{parameter.Name}' in 'ext' signatures"));
		}
		else if (HasDestructor(type))
		{
			var (located, _, location) = Locate(type, node, HasDestructor);
			if (IsOwn(location))
				diagnostics.Add(Report(location, "Cannot use types with destructors in 'ext' signatures", located,
					typePool.FindDestructor(located)!));
		}
	}
	
	private static IEnumerable<SourceLocation> FindNestedExtFunctions(TypeSymbol type, ITypeNode node) =>
		GetWrittenParts(type, node).SelectMany(static part => part switch
		{
			(FunctionType { IsExternal: true }, FunctionTypeNode { IsExternal: true } function, _) =>
				[function.SourceLocation],
			(var partType, { } partNode, _) => FindNestedExtFunctions(partType, partNode),
			_ => []
		});
	
	private bool HasDestructor(TypeSymbol type) => typePool.FindDestructor(type) is not null;
	
	private bool HasPlainEnum(TypeSymbol type) => typePool.FindPlainEnum(type) is not null;
	
	private static string GetWrittenName(TypeSymbol located, ITypeNode? node) =>
		located is FunctionType function && node is FunctionTypeNode { IsRef: false }
			? function.PlainName
			: located.Name;
	
	private bool IsForeign(TypeSymbol type) => typePool.FindForeign(type) is not null;
	
	public static TypeParameterSymbol? FindUnconstrained(TypeSymbol type) => type switch
	{
		TypeParameterSymbol { IsValue: false, IsExt: false } parameter => parameter,
		PointerType pointer => FindUnconstrained(pointer.BaseType),
		BorrowType borrow => FindUnconstrained(borrow.Target),
		ArrayType array => FindUnconstrained(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Select(FindUnconstrained)
			.FirstOrDefault(static found => found is not null),
		NamedTypeSymbol named => named.TypeArguments
			.Select(FindUnconstrained)
			.FirstOrDefault(static found => found is not null),
		_ => null
	};
	
	public static Diagnostic Report(SourceLocation location, string message, TypeSymbol located, TypeSymbol offender) =>
		new(DiagnosticSeverity.Error, location, message)
		{
			Hints = offender == located ? [] : [$"'{located.Name}' contains '{offender.Name}'"]
		};
	
	public static (TypeSymbol Type, ITypeNode? Node, SourceLocation Location) Locate(TypeSymbol type, ITypeNode node,
		Func<TypeSymbol, bool> isOffending)
	{
		foreach (var (part, partNode, location) in GetWrittenParts(type, node))
		{
			if (isOffending(part))
				return partNode is null ? (part, null, location) : Locate(part, partNode, isOffending);
		}
		
		return (type, node, node.SourceLocation);
	}
	
	private static IEnumerable<(TypeSymbol Type, ITypeNode? Node, SourceLocation Location)> GetWrittenParts(
		TypeSymbol type, ITypeNode node)
	{
		switch (type, node)
		{
			case (FunctionType function, FunctionTypeNode syntax):
				for (var i = 0; i < syntax.ParameterTypes.Length && i < function.ParameterTypes.Length; i++)
					yield return (function.GetDeclaredType(i), syntax.ParameterTypes[i],
						syntax.ParameterTypes[i].SourceLocation);
				
				if (syntax.ReturnType is { } returnType)
					yield return (function.ReturnType, returnType, returnType.SourceLocation);
				
				break;
			
			case (ArrayType array, GenericTypeNode { Arguments: [var element, ..] }):
				yield return (array.ElementType, (element as TypeArgumentNode)?.Type, element.SourceLocation);
				break;
			
			case (PointerType pointer, GenericTypeNode { Arguments: [var target] }):
				yield return (pointer.BaseType, (target as TypeArgumentNode)?.Type, target.SourceLocation);
				break;
			
			case (NamedTypeSymbol named, GenericTypeNode generic):
				for (var i = 0; i < generic.Arguments.Length && i < named.TypeArguments.Length; i++)
					yield return (named.TypeArguments[i], (generic.Arguments[i] as TypeArgumentNode)?.Type,
						generic.Arguments[i].SourceLocation);
				
				break;
		}
	}
}