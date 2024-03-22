using Cella.Analysis.Text;
using Cella.Diagnostics;

namespace Cella.Analysis.Semantics;

public sealed class CollectionException : Exception
{
	public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

	private readonly IBuffer source;
	private readonly TextRange range;
	
	public CollectionException(string? message, Token token, TextRange? range = null)
		: base(message)
	{
		source = token.Source;
		this.range = range ?? token.Range;
	}
	
	public CollectionException(string? message, IBuffer source, TextRange range)
		: base(message)
	{
		this.source = source;
		this.range = range;
	}
	
	public static implicit operator Diagnostic(CollectionException collectionException)
	{
		return new Diagnostic(collectionException.Severity, collectionException.source, collectionException.range,
			collectionException.Message);
	}
}

/// <summary>
/// An empty class used to fail collection without explicit diagnostics. This is typically used when diagnostics have
/// already been reported and handled.
/// </summary>
public sealed class CollectionFailedException : Exception
{
}