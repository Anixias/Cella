using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class ExtSignatureTypes(TypePool typePool)
{
	private readonly List<(TypeSymbol Type, ITypeNode Node)> _types = [];
	
	public void Add(TypeSymbol type, ITypeNode node) => _types.Add((type, node));
	
	public void ReportDestructors(DiagnosticList diagnostics)
	{
		foreach (var (type, node) in _types)
		{
			if (!HasDestructor(type))
				continue;
			
			var (located, location) = Locate(type, node, HasDestructor);
			diagnostics.Add(Report(location, "Cannot use types with destructors in 'ext' signatures", located,
				typePool.FindDestructor(located)!));
		}
		
		_types.Clear();
	}
	
	private bool HasDestructor(TypeSymbol type) => typePool.FindDestructor(type) is not null;
	
	public static Diagnostic Report(SourceLocation location, string message, TypeSymbol located, TypeSymbol offender) =>
		new(DiagnosticSeverity.Error, location, message)
		{
			Hints = offender == located ? [] : [$"'{located.Name}' contains '{offender.Name}'"]
		};
	
	public static (TypeSymbol Type, SourceLocation Location) Locate(TypeSymbol type, ITypeNode node,
		Func<TypeSymbol, bool> isOffending)
	{
		foreach (var (part, partNode, location) in GetWrittenParts(type, node))
		{
			if (isOffending(part))
				return partNode is null ? (part, location) : Locate(part, partNode, isOffending);
		}
		
		return (type, node.SourceLocation);
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
		}
	}
}