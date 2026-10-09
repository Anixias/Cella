using System.Collections.Immutable;
using Cella.Compiler.Linking;
using Cella.Core.Syntax;
using Cella.Core.Text;
using CsToml;

namespace Cella.Compiler.Projects;

[TomlSerializedObject]
public sealed partial class CellaProject
{
	public const string ProjectFileExtension = ".celp";
	public const string SourceFileExtension = ".ce";
	private const string ProjectSearchPattern = $"*{ProjectFileExtension}";
	private const string SourceSearchPattern = $"*{SourceFileExtension}";
	
	[TomlValueOnSerialized]
	public required ProjectOutputType OutputType { get; init; }
	
	[TomlValueOnSerialized]
	public SystemLinkPreference SystemLinkPreference { get; init; }
	
	[TomlValueOnSerialized]
	public string? AssemblyName { get; init; }
	
	[TomlValueOnSerialized]
	public bool? BoundsChecks { get; init; }
	
	[TomlValueOnSerialized]
	public bool? OverflowChecks { get; init; }
	
	[TomlValueOnSerialized]
	public List<ProjectReference>? ProjectReferences { get; init; }
	
	[TomlValueOnSerialized]
	public Dictionary<string, bool>? Flags { get; init; }
	
	public Dictionary<string, ProjectSettings> When { get; } = [];
	
	public static IEnumerable<string> FindProjects(string directory) =>
		Directory.EnumerateFiles(directory, ProjectSearchPattern, SearchOption.AllDirectories);
	
	// All cella files in or under the project directory belong to that project
	public static IEnumerable<string> FindSourceFiles(string directory) => FindSourceFiles(directory, true);
	
	public static async Task<CellaProject> LoadAsync(Stream stream)
	{
		var document = await CsTomlSerializer.DeserializeAsync<TomlDocument>(stream);
		var project = document.RootNode.GetValue<CellaProject>();
		foreach (var (condition, settings) in document.RootNode["When"u8])
			project.When[condition.ToString()!] = settings.GetValue<ProjectSettings>();
		
		return project;
	}
	
	public CellaProject Resolve(string path, IReadOnlyDictionary<string, bool> flags, List<string> errors)
	{
		var matching = new List<(string Condition, ProjectSettings Settings)>();
		foreach (var (condition, settings) in When)
		{
			if (settings.OutputType is not null)
				errors.Add($"{path}: Cannot set '{nameof(OutputType)}' in {DescribeTable(condition)}");
			
			if (settings.Flags is { Count: > 0 })
				errors.Add($"{path}: Cannot set '{nameof(Flags)}' in {DescribeTable(condition)}");
			
			var tokens = new FilteredScanner(new StringSource(condition, path)).ToImmutableArray();
			var parser = new FileParser(tokens, Path.GetFileName(path), path, flags);
			if (parser.ParseCondition() == true)
				matching.Add((condition, settings));
			
			errors.AddRange(parser.Diagnostics.Select(diagnostic =>
				$"{path}: {diagnostic.Message} in {DescribeTable(condition)}"));
		}
		
		return new CellaProject
		{
			OutputType = OutputType,
			SystemLinkPreference = Override(path, matching, static settings => settings.SystemLinkPreference,
				nameof(SystemLinkPreference), errors) ?? SystemLinkPreference,
			AssemblyName = Override(path, matching, static settings => settings.AssemblyName, nameof(AssemblyName),
				errors) ?? AssemblyName,
			BoundsChecks = Override(path, matching, static settings => settings.BoundsChecks, nameof(BoundsChecks),
				errors) ?? BoundsChecks,
			OverflowChecks = Override(path, matching, static settings => settings.OverflowChecks,
				nameof(OverflowChecks), errors) ?? OverflowChecks,
			ProjectReferences =
			[
				..ProjectReferences ?? [],
				..matching.SelectMany(static entry => entry.Settings.ProjectReferences ?? [])
			],
			Flags = Flags
		};
	}
	
	private static T? Override<T>(string path, List<(string Condition, ProjectSettings Settings)> matching,
		Func<ProjectSettings, T?> select, string name, List<string> errors)
	{
		var setters = matching.Where(entry => select(entry.Settings) is not null).ToList();
		if (setters.Count > 1)
			errors.Add($"{path}: '{name}' is set more than once, by " +
			           $"{DescribeTables(setters.Select(static entry => entry.Condition).ToList())}");
		
		return setters.Count == 1 ? select(setters[0].Settings) : default;
	}
	
	private static string DescribeTable(string condition) => $"[When.\"{condition}\"]";
	
	private static string DescribeTables(List<string> conditions) =>
		$"{string.Join(", ", conditions.SkipLast(1).Select(DescribeTable))} and {DescribeTable(conditions[^1])}";
	
	private static IEnumerable<string> FindSourceFiles(string directory, bool allowProjectFile)
	{
		if (!allowProjectFile && Directory.EnumerateFiles(directory, ProjectSearchPattern).Any())
			yield break;
		
		foreach (var file in Directory.EnumerateFiles(directory, SourceSearchPattern))
			yield return file;
		
		foreach (var subdirectory in Directory.EnumerateDirectories(directory))
			foreach (var file in FindSourceFiles(subdirectory, false))
				yield return file;
	}
}

[TomlSerializedObject]
public sealed partial class ProjectSettings
{
	[TomlValueOnSerialized]
	public ProjectOutputType? OutputType { get; init; }
	
	[TomlValueOnSerialized]
	public SystemLinkPreference? SystemLinkPreference { get; init; }
	
	[TomlValueOnSerialized]
	public string? AssemblyName { get; init; }
	
	[TomlValueOnSerialized]
	public bool? BoundsChecks { get; init; }
	
	[TomlValueOnSerialized]
	public bool? OverflowChecks { get; init; }
	
	[TomlValueOnSerialized]
	public List<ProjectReference>? ProjectReferences { get; init; }
	
	[TomlValueOnSerialized]
	public Dictionary<string, bool>? Flags { get; init; }
}

[TomlSerializedObject]
public sealed partial record ProjectReference
{
	[TomlValueOnSerialized]
	public required string Path { get; init; }
}