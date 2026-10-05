using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class ExtSignatureTypes(TypePool typePool)
{
	private readonly List<(TypeSymbol Type, SourceLocation Location)> _types = [];
	
	public void Add(TypeSymbol type, SourceLocation location) => _types.Add((type, location));
	
	public void ReportDestructors(DiagnosticList diagnostics)
	{
		foreach (var (type, location) in _types)
		{
			if (typePool.FindDestructor(type) is not { } destructor)
				continue;
			
			var name = destructor == type ? $"'{type.Name}'" : $"'{destructor.Name}' in '{type.Name}'";
			diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"{name} has a destructor, so it can't be part of an 'ext' signature"));
		}
		
		_types.Clear();
	}
}