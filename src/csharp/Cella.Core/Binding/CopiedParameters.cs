using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class CopiedParameters(TypePool typePool)
{
	private readonly List<(TypeSymbol Type, SourceLocation Location)> _parameters = [];
	
	public void Add(TypeSymbol type, SourceLocation location) => _parameters.Add((type, location));
	
	public void Report(DiagnosticList diagnostics)
	{
		foreach (var (type, location) in _parameters)
		{
			if (type is not InvalidType && !typePool.IsCopy(type))
				diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot copy '{type.Name}'"));
		}
		
		_parameters.Clear();
	}
}
