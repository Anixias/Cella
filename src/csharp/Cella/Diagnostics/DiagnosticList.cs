using System.Collections;

namespace Cella.Diagnostics;

public sealed class DiagnosticList : IEnumerable<Diagnostic>
{
	public int Count => diagnostics.Count;

	public DiagnosticList Errors => new(diagnostics.Where(d => d.severity == DiagnosticSeverity.Error));
	public DiagnosticList Warnings => new(diagnostics.Where(d => d.severity == DiagnosticSeverity.Warning));
	
	public int ErrorCount => diagnostics.Count(d => d.severity == DiagnosticSeverity.Error);
	public int WarningCount => diagnostics.Count(d => d.severity == DiagnosticSeverity.Warning);
	
	private readonly List<Diagnostic> diagnostics;

	public DiagnosticList()
	: this([])
	{
	}

	public DiagnosticList(IEnumerable<Diagnostic> diagnostics)
	{
		this.diagnostics = diagnostics.ToList();
	}

	public void Clear()
	{
		diagnostics.Clear();
	}
	
	public void Add(Diagnostic diagnostic)
	{
		diagnostics.Add(diagnostic);
	}
	
	public void Add(IEnumerable<Diagnostic> diagnostic)
	{
		diagnostics.AddRange(diagnostic);
	}
	
	public IEnumerator<Diagnostic> GetEnumerator()
	{
		return diagnostics.OrderBy(d => d.line).ThenBy(d => d.column).GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}

	public DiagnosticList OfSeverity(DiagnosticSeverity severity)
	{
		return new DiagnosticList(diagnostics.Where(d => d.severity == severity));
	}
}