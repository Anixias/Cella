using Cella.Core.Text;

namespace Cella.Diagnostics;

public enum DiagnosticSeverity
{
	Error,
	Warning,
	Hint
}

public sealed record Diagnostic
{
	public DiagnosticSeverity Severity { get; }
	public SourceLocation SourceLocation { get; }
	public string Message { get; }
	public int Line { get; }
	public int Column { get; }
	
	public Diagnostic(DiagnosticSeverity severity, SourceLocation sourceLocation, string message)
	{
		Severity = severity;
		SourceLocation = sourceLocation;
		Message = message;
		
		if (sourceLocation.Range.IsEmpty)
		{
			Line = 0;
			Column = 0;
			return;
		}
		
		(Line, Column) = sourceLocation.GetLineColumn();
	}
	
	public override string ToString() => Message;
}