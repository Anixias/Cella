using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed class ParseException(Diagnostic diagnostic) : InvalidOperationException(diagnostic.Message)
{
	public Diagnostic Diagnostic { get; } = diagnostic;
}