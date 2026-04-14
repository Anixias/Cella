using System.Collections;
using Cella.Core;

namespace Cella.Diagnostics;

public sealed class DiagnosticList : IEnumerable<Diagnostic>
{
	public int Count => _diagnostics.Count;
	
	public IEnumerable<Diagnostic> Errors => _errors.Values.SelectMany(static d => d);
	public IEnumerable<Diagnostic> Warnings => _warnings.Values.SelectMany(static d => d);
	
	public int ErrorCount => _errors.Count;
	public int WarningCount => _warnings.Count;
	
	private readonly SortedList<LineColumnKey, List<Diagnostic>> _diagnostics = [];
	private readonly SortedList<LineColumnKey, List<Diagnostic>> _errors = [];
	private readonly SortedList<LineColumnKey, List<Diagnostic>> _warnings = [];
	
	public DiagnosticList() : this([])
	{
	}
	
	public DiagnosticList(IEnumerable<Diagnostic> diagnostics)
	{
		AddRange(diagnostics);
	}
	
	public void Clear()
	{
		_diagnostics.Clear();
		_errors.Clear();
		_warnings.Clear();
	}
	
	public void Add(Diagnostic diagnostic)
	{
		var key = new LineColumnKey(diagnostic.Line, diagnostic.Column);
		_diagnostics.GetOrAdd(key).Add(diagnostic);
		
		switch (diagnostic.Severity)
		{
			case DiagnosticSeverity.Error:
				_errors.GetOrAdd(key).Add(diagnostic);
				break;
			
			case DiagnosticSeverity.Warning:
				_warnings.GetOrAdd(key).Add(diagnostic);
				break;
		}
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