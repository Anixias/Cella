using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class ExtSignatureTypes(TypePool typePool)
{
	private readonly List<(TypeSymbol Type, ITypeNode Node)> _types = [];
	
	public void Add(TypeSymbol type, ITypeNode node) => _types.Add((type, node));
	
	public void Report(DiagnosticList diagnostics)
	{
		var reported = new HashSet<SourceLocation>();
		foreach (var (type, node) in _types)
		{
			if (FindForeign(type) is { } foreign)
			{
				var (located, locatedNode, location) = Locate(type, node, IsForeign);
				if (reported.Add(location))
					diagnostics.Add(new(DiagnosticSeverity.Error, location,
						$"Cannot use '{GetWrittenName(foreign, located, locatedNode)}' in 'ext' signatures"));
			}
			else if (FindUnconstrained(type) is { } parameter)
			{
				var (_, _, location) = Locate(type, node, static part => FindUnconstrained(part) is not null);
				if (reported.Add(location))
					diagnostics.Add(new(DiagnosticSeverity.Error, location,
						$"Cannot use '{parameter.Name}' in 'ext' signatures"));
			}
			else if (HasDestructor(type))
			{
				var (located, _, location) = Locate(type, node, HasDestructor);
				if (reported.Add(location))
					diagnostics.Add(Report(location, "Cannot use types with destructors in 'ext' signatures", located,
						typePool.FindDestructor(located)!));
			}
		}
		
		_types.Clear();
	}
	
	private bool HasDestructor(TypeSymbol type) => typePool.FindDestructor(type) is not null;
	
	private static string GetWrittenName(TypeSymbol foreign, TypeSymbol located, ITypeNode? node) =>
		foreign is FunctionType function && foreign == located && node is FunctionTypeNode { IsRef: false }
			? function.PlainName
			: foreign.Name;
	
	private static bool IsForeign(TypeSymbol type) => FindForeign(type) is not null;
	
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
	
	public static TypeSymbol? FindForeign(TypeSymbol type) => type switch
	{
		DynType or FStrType or FunctionType { IsExternal: false } => type,
		PointerType pointer => FindDyn(pointer.BaseType),
		BorrowType borrow => FindForeign(borrow.Target),
		ArrayType array => FindForeign(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Select(FindForeign)
			.FirstOrDefault(static foreign => foreign is not null),
		_ => null
	};
	
	private static TypeSymbol? FindDyn(TypeSymbol type) => type switch
	{
		DynType or FStrType => type,
		PointerType pointer => FindDyn(pointer.BaseType),
		BorrowType borrow => FindDyn(borrow.Target),
		ArrayType array => FindDyn(array.ElementType),
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