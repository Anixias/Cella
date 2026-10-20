using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class CopiedParameters(TypePool typePool)
{
	private readonly List<(TypeSymbol Type, SourceLocation Location, ErrorContainment.Owner Owner)> _parameters = [];
	
	public ErrorContainment? Containment { get; set; }
	
	public void Add(TypeSymbol type, SourceLocation location) =>
		_parameters.Add((type, location, Containment?.Current ?? default));
	
	public void Report(DiagnosticList diagnostics)
	{
		foreach (var (type, location, owner) in _parameters)
		{
			if (type is InvalidType || typePool.IsCopy(type))
				continue;
			
			Containment?.Enter(owner);
			diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot copy '{type.Name}'"));
			Containment?.Exit();
		}
		
		_parameters.Clear();
	}
}