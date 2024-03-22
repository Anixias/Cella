using Cella.Analysis.Text;
using Cella.Diagnostics;

namespace Cella.Analysis.Semantics;

public sealed class ResolutionException : Exception
{
	public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

	private readonly IBuffer source;
	private readonly TextRange range;
	
	public ResolutionException(string? message, Token token, TextRange? range = null)
		: base(message)
	{
		source = token.Source;
		this.range = range ?? token.Range;
	}
	
	public ResolutionException(string? message, IBuffer source, TextRange range)
		: base(message)
	{
		this.source = source;
		this.range = range;
	}
	
	public static implicit operator Diagnostic(ResolutionException resolutionException)
	{
		return new Diagnostic(resolutionException.Severity, resolutionException.source, resolutionException.range,
			resolutionException.Message);
	}
}

/// <summary>
/// An empty class used to fail resolution without explicit diagnostics. This is typically used when diagnostics have
/// already been reported and handled.
/// </summary>
public sealed class ResolutionFailedException : Exception
{
}