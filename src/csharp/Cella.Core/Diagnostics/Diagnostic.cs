using System.Collections.Immutable;
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
	public DiagnosticSeverity Severity { get; private init; }
	public SourceLocation SourceLocation { get; }
	public string Message { get; }
	public ImmutableArray<string> Hints { get; init; } = ImmutableArray<string>.Empty;
	public ImmutableArray<DiagnosticFix> Fixes { get; init; } = [];
	public string? Lint { get; init; }
	public SourceLocation? UnnecessaryLocation { get; init; }
	public int Line { get; }
	public int Column { get; }
	
	public Diagnostic(DiagnosticSeverity severity, SourceLocation sourceLocation, string message)
	{
		Severity = severity;
		SourceLocation = sourceLocation;
		Message = message;
		
		if (!sourceLocation.Range.IsValid)
		{
			Line = int.MaxValue;
			Column = 0;
			return;
		}
		
		(Line, Column) = sourceLocation.GetLineColumn();
	}
	
	public Diagnostic WithSeverity(DiagnosticSeverity severity) => this with { Severity = severity };
	
	public override string ToString() => Message;
}