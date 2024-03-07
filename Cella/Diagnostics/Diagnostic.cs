using Cella.Analysis.Text;

namespace Cella.Diagnostics;

public enum DiagnosticSeverity
{
	Error,
	Warning,
	Hint
}

public readonly struct Diagnostic
{
	public readonly DiagnosticSeverity severity;
	public readonly IBuffer source;
	public readonly TextRange? range;
	public readonly string message;
	public readonly int line;
	public readonly int column;

	public Diagnostic(DiagnosticSeverity severity, IBuffer source, TextRange? range, string message)
	{
		this.severity = severity;
		this.source = source;
		this.range = range;
		this.message = message;

		if (range is null)
		{
			line = 0;
			column = 0;
			return;
		}

		(line, column) = source.GetLineColumn(range.Value.Start);
	}

	public override string ToString()
	{
		return message;
	}
}