using System.Diagnostics.CodeAnalysis;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed record DiagnosticText(string Message, string? Hint = null)
{
	[return: NotNullIfNotNull(nameof(message))]
	public static implicit operator DiagnosticText?(string? message) => message is null ? null : new(message);
	
	public Diagnostic At(SourceLocation location) =>
		new(DiagnosticSeverity.Error, location, Message) { Hints = Hint is null ? [] : [Hint] };
}