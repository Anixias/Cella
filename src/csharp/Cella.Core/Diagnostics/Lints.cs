using System.Collections.Immutable;

namespace Cella.Diagnostics;

public enum LintLevel
{
	Off,
	Hint,
	Warning,
	Error
}

public sealed record LintInfo(string Name, string Label, LintLevel Default);

public static class Lints
{
	public const string Redundant = nameof(Redundant);
	public const string RedundantImport = nameof(RedundantImport);
	public const string NeverChanged = nameof(NeverChanged);
	public const string UnreachableCode = nameof(UnreachableCode);
	public const string FloatPattern = nameof(FloatPattern);
	public const string SelfConversion = nameof(SelfConversion);
	public const string UnusedLocal = nameof(UnusedLocal);
	public const string UnusedDeclaration = nameof(UnusedDeclaration);
	public const string Naming = nameof(Naming);
	
	public static ImmutableArray<LintInfo> All { get; } =
	[
		new(Redundant, "Redundant markers", LintLevel.Hint),
		new(RedundantImport, "Redundant imports", LintLevel.Hint),
		new(NeverChanged, "Variables that are never changed", LintLevel.Hint),
		new(UnreachableCode, "Unreachable code", LintLevel.Warning),
		new(FloatPattern, "Float patterns", LintLevel.Warning),
		new(SelfConversion, "Conversions that call themselves", LintLevel.Warning),
		new(UnusedLocal, "Unused local variables", LintLevel.Hint),
		new(UnusedDeclaration, "Unused functions and globals", LintLevel.Hint),
		new(Naming, "Naming rules", LintLevel.Hint)
	];
	
	public static LintInfo? Find(string name) => All.FirstOrDefault(lint => lint.Name == name);
	
	public static Diagnostic? Apply(Diagnostic diagnostic, IReadOnlyDictionary<string, LintLevel> levels)
	{
		if (diagnostic.Lint is not { } lint || !levels.TryGetValue(lint, out var level))
			return diagnostic;
		
		return level switch
		{
			LintLevel.Off => null,
			LintLevel.Hint => diagnostic.WithSeverity(DiagnosticSeverity.Hint),
			LintLevel.Warning => diagnostic.WithSeverity(DiagnosticSeverity.Warning),
			_ => diagnostic.WithSeverity(DiagnosticSeverity.Error)
		};
	}
}