using Cella.Analysis.Text;
using Cella.Diagnostics;

namespace Cella.Analysis.Syntax;

public class ParseException : Exception
{
	public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;
	private readonly IBuffer source;
	private readonly TextRange range;

	public ParseException(string? message, Token token, TextRange? range = null)
		: base(message)
	{
		source = token.Source;
		this.range = range ?? token.Range;
	}

	public ParseException(string? message, IBuffer source, TextRange range)
		: base(message)
	{
		this.source = source;
		this.range = range;
	}
	
	public static implicit operator Diagnostic(ParseException parseException)
	{
		return new Diagnostic(parseException.Severity, parseException.source, parseException.range,
			parseException.Message);
	}
}