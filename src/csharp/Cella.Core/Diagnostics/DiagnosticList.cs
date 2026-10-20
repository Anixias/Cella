using System.Collections;
using Cella.Core;

namespace Cella.Diagnostics;

public sealed class DiagnosticList : IEnumerable<Diagnostic>
{
	public int Count { get; private set; }
	public int ErrorCount { get; private set; }
	
	private readonly SortedList<LineColumnKey, List<Diagnostic>> _diagnostics = [];
	private List<Diagnostic>? redirect;
	
	public IDiagnosticGate? Gate { get; set; }
	
	public DiagnosticList() : this([])
	{
	}
	
	public DiagnosticList(IEnumerable<Diagnostic> diagnostics)
	{
		AddRange(diagnostics);
	}
	
	public List<Diagnostic>? Redirect(List<Diagnostic>? target)
	{
		var previous = redirect;
		redirect = target;
		return previous;
	}
	
	public void Add(Diagnostic diagnostic)
	{
		if (Gate?.Admit(diagnostic) == false)
			return;
		
		if (redirect is { } target)
		{
			target.Add(diagnostic);
			return;
		}
		
		Gate?.Record(diagnostic);
		var key = new LineColumnKey(diagnostic.Line, diagnostic.Column);
		_diagnostics.GetOrAdd(key).Add(diagnostic);
		Count++;
		
		if (diagnostic.Severity == DiagnosticSeverity.Error)
			ErrorCount++;
	}
	
	public void AddRange(params IEnumerable<Diagnostic> diagnostics)
	{
		foreach (var diagnostic in diagnostics)
			Add(diagnostic);
	}
	
	public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.Values.SelectMany(static d => d).GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	
	private readonly record struct LineColumnKey(int Line, int Column) : IComparable<LineColumnKey>
	{
		public int CompareTo(LineColumnKey other)
		{
			var lineComparison = Line.CompareTo(other.Line);
			if (lineComparison != 0)
				return lineComparison;
			
			return Column.CompareTo(other.Column);
		}
	}
}