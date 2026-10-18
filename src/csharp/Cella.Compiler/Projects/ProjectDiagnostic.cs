using System.Collections.Immutable;
using Cella.Diagnostics;

namespace Cella.Compiler.Projects;

public sealed record ProjectDiagnostic(DiagnosticSeverity Severity, string Message, ImmutableArray<string> Projects)
{
	public int Line { get; private init; }
	
	public bool StopsChecking { get; init; }
	
	private bool IsInProject { get; init; }
	
	public string Text => !IsInProject ? Message
		: Line > 0 ? $"{Projects[0]}:{Line}: {Message}"
		: $"{Projects[0]}: {Message}";
	
	public static ProjectDiagnostic In(string path, string message) =>
		new(DiagnosticSeverity.Error, message, [path]) { IsInProject = true };
	
	public static ProjectDiagnostic At(string path, int line, string message) =>
		new(DiagnosticSeverity.Error, message, [path]) { IsInProject = true, Line = line };
	
	public static ProjectDiagnostic About(string message, params IEnumerable<string> paths) =>
		new(DiagnosticSeverity.Error, message, [..paths.Distinct()]);
}