using System.Collections.Immutable;
using Cella.Compiler.Linking;

namespace Cella.Compiler.Projects;

public enum SettingKind
{
	Choice,
	Boolean,
	Text,
	Version,
	TextList,
	Flags,
	References
}

public sealed record SettingInfo(string Name, string Label, SettingKind Kind, bool IsConditional, string? Default)
{
	public ImmutableArray<string> Choices { get; init; } = [];
	public bool DefaultsToProjectName { get; init; }
}

public static class ProjectSchema
{
	public static ImmutableArray<SettingInfo> Settings { get; } =
	[
		Choice<ProjectOutputType>(nameof(CellaProject.OutputType), "Output type", false, null),
		new(nameof(CellaProject.CellaVersion), "Cella version", SettingKind.Version, false, "Newest installed"),
		Choice<StandardLibrary>(nameof(CellaProject.StandardLibrary), "Standard library", false,
			default(StandardLibrary).ToString()),
		Choice<SystemLinkPreference>(nameof(CellaProject.SystemLinkPreference), "System link preference", true,
			default(SystemLinkPreference).ToString()),
		new(nameof(CellaProject.AssemblyName), "Assembly name", SettingKind.Text, true, null)
		{
			DefaultsToProjectName = true
		},
		new(nameof(CellaProject.BoundsChecks), "Bounds checks", SettingKind.Boolean, true, "On"),
		new(nameof(CellaProject.OverflowChecks), "Overflow checks", SettingKind.Boolean, true, "On in debug builds"),
		new(nameof(CellaProject.DebugInfo), "Debug info", SettingKind.Boolean, true, "On"),
		new(nameof(CellaProject.Referenceable), "Referenceable", SettingKind.Boolean, true, "Off"),
		new(nameof(CellaProject.Flags), "Flags", SettingKind.Flags, false, null),
		new(nameof(CellaProject.Imports), "Imports", SettingKind.TextList, false, null),
		new(nameof(CellaProject.Prelude), "Prelude", SettingKind.TextList, false, null),
		new(nameof(CellaProject.ProjectReferences), "Project references", SettingKind.References, true, null)
	];
	
	public static SettingInfo? Find(string name) => Settings.FirstOrDefault(setting => setting.Name == name);
	
	private static SettingInfo Choice<T>(string name, string label, bool isConditional, string? defaultValue)
		where T : struct, Enum => new(name, label, SettingKind.Choice, isConditional, defaultValue)
	{
		Choices = [..Enum.GetNames<T>()]
	};
}