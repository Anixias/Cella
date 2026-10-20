using System.Collections.Immutable;
using System.Text;
using Cella.Compiler.Linking;
using Cella.Core.Syntax;
using Cella.Core.Text;
using CsToml;
using CsToml.Error;
using CsToml.Values;

namespace Cella.Compiler.Projects;

[TomlSerializedObject]
public sealed partial class CellaProject
{
	public const string ProjectFileExtension = ".celp";
	public const string SourceFileExtension = ".ce";
	private const string ProjectSearchPattern = $"*{ProjectFileExtension}";
	private const string SourceSearchPattern = $"*{SourceFileExtension}";
	
	private static readonly HashSet<string> _whenSettingNames =
	[
		..ProjectSchema.Settings.Where(static setting => setting.IsConditional).Select(static setting => setting.Name),
		nameof(OutputType), nameof(Flags)
	];
	
	private static readonly HashSet<string> _settingNames =
		[..ProjectSchema.Settings.Select(static setting => setting.Name), nameof(When)];
	
	public static bool IsSettingPath(IReadOnlyList<string> path) => path switch
	{
		[var name] => _settingNames.Contains(name),
		[nameof(ProjectReferences), var name] => IsReferenceSetting(name),
		[nameof(When), _, nameof(ProjectReferences), var name] => IsReferenceSetting(name),
		[nameof(When), _, var name] => _whenSettingNames.Contains(name),
		_ => false
	};
	
	private static bool IsReferenceSetting(string name) =>
		name is nameof(ProjectReference.Path) or nameof(ProjectReference.Alias);
	
	[TomlValueOnSerialized]
	public required ProjectOutputType OutputType { get; init; }
	
	[TomlValueOnSerialized]
	public string? CellaVersion { get; init; }
	
	[TomlValueOnSerialized]
	public StandardLibrary StandardLibrary { get; init; }
	
	[TomlValueOnSerialized]
	public SystemLinkPreference SystemLinkPreference { get; init; }
	
	[TomlValueOnSerialized]
	public string? AssemblyName { get; init; }
	
	[TomlValueOnSerialized]
	public bool? BoundsChecks { get; init; }
	
	[TomlValueOnSerialized]
	public bool? OverflowChecks { get; init; }
	
	[TomlValueOnSerialized]
	public bool? DebugInfo { get; init; }
	
	[TomlValueOnSerialized]
	public bool? Referenceable { get; init; }
	
	[TomlValueOnSerialized]
	public List<ProjectReference>? ProjectReferences { get; init; }
	
	[TomlValueOnSerialized]
	public Dictionary<string, bool>? Flags { get; init; }
	
	[TomlValueOnSerialized]
	public List<string>? Imports { get; init; }
	
	[TomlValueOnSerialized]
	public List<string>? Prelude { get; init; }
	
	public Dictionary<string, ProjectSettings> When { get; } = [];
	
	public ImmutableArray<CelpEntry> ImportEntries { get; private set; } = [];
	
	public static IEnumerable<string> FindProjects(string directory) =>
		Directory.EnumerateFiles(directory, ProjectSearchPattern, SearchOption.AllDirectories);
	
	public static IEnumerable<string> FindSiblingProjects(string path) => Directory
		.EnumerateFiles(Path.GetDirectoryName(path) ?? ".", ProjectSearchPattern)
		.Where(other => !Path.GetFullPath(other).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
		.Select(Path.GetFileName)
		.OfType<string>()
		.Order(StringComparer.Ordinal);
	
	// All cella files in or under the project directory belong to that project
	public static IEnumerable<string> FindSourceFiles(string directory) => FindSourceFiles(directory, true);
	
	public static bool IsProjectFile(string path) =>
		Path.GetExtension(path).Equals(ProjectFileExtension, StringComparison.OrdinalIgnoreCase);
	
	public static async Task<CellaProject?> LoadAsync(Stream stream, string path, List<ProjectDiagnostic> errors)
	{
		var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer);
		var bytes = buffer.ToArray();
		var reader = CelpReader.Read(Encoding.UTF8.GetString(bytes.AsSpan(bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0)));
		if (reader.Errors.Count > 0)
		{
			errors.AddRange(reader.Errors.Select(error => ProjectDiagnostic.At(path, error.Line, error.Message)));
			return null;
		}
		
		using var content = new MemoryStream(bytes);
		TomlDocument document;
		try
		{
			document = await CsTomlSerializer.DeserializeAsync<TomlDocument>(content);
		}
		catch (CsTomlSerializeException e)
		{
			errors.AddRange((e.ParseExceptions ?? []).Select(error =>
				ProjectDiagnostic.At(path, (int)error.LineNumber, "Unsupported TOML")));
			
			return null;
		}
		
		var root = document.RootNode;
		ReportUnknownSettings(root, _settingNames, null, path, errors);
		var reported = errors.Count;
		CheckTypes(root, null, path, errors);
		if (!root["OutputType"u8].HasValue)
			errors.Add(ProjectDiagnostic.In(path, $"Missing '{nameof(OutputType)}'"));
		
		if (errors.Count > reported)
			return null;
		
		var project = root.GetValue<CellaProject>();
		project.ImportEntries = [..reader.ImportEntries];
		project.ProjectReferences?.RemoveAll(static r => r.Path is null);
		
		foreach (var (key, node) in root["When"u8])
		{
			var condition = key.ToString()!;
			ReportUnknownSettings(node, _whenSettingNames, condition, path, errors);
			var settings = node.GetValue<ProjectSettings>();
			settings.ProjectReferences?.RemoveAll(static r => r.Path is null);
			project.When[condition] = settings;
		}
		
		return project;
	}
	
	private static void CheckTypes(TomlDocumentNode node, string? condition, string path,
		List<ProjectDiagnostic> errors)
	{
		var place = condition is null ? "" : $" in {DescribeTable(condition)}";
		foreach (var (key, value) in node)
		{
			var name = key.ToString()!;
			switch (name)
			{
				case nameof(OutputType):
					CheckChoice<ProjectOutputType>(value, name, place, path, errors);
					break;
				
				case nameof(SystemLinkPreference):
					CheckChoice<SystemLinkPreference>(value, name, place, path, errors);
					break;
				
				case nameof(StandardLibrary):
					CheckChoice<StandardLibrary>(value, name, place, path, errors);
					break;
				
				case nameof(CellaVersion) when value.ValueType != TomlValueType.String:
					errors.Add(ProjectDiagnostic.In(path, $"'{name}' must be a string"));
					break;
				
				case nameof(CellaVersion) when SemanticVersion.Parse(value.GetString()) is null:
					errors.Add(ProjectDiagnostic.In(path, $"'{value.GetString()}' is not a valid '{name}'"));
					break;
				
				case nameof(AssemblyName) when value.ValueType != TomlValueType.String:
					errors.Add(ProjectDiagnostic.In(path, $"'{name}'{place} must be a string"));
					break;
				
				case nameof(AssemblyName) when !IsFileName(value.GetString()):
					errors.Add(ProjectDiagnostic.In(path, $"'{name}'{place} must be a file name"));
					break;
				
				case nameof(BoundsChecks) or nameof(OverflowChecks) or nameof(DebugInfo) or nameof(Referenceable)
					when value.ValueType != TomlValueType.Boolean:
					errors.Add(ProjectDiagnostic.In(path, $"'{name}'{place} must be true or false"));
					break;
				
				case nameof(ProjectReferences):
					CheckReferences(value, condition, path, errors);
					break;
				
				case nameof(Flags):
					CheckFlags(value, condition, path, errors);
					break;
				
				case nameof(Imports) or nameof(Prelude) when !IsStringList(value):
					errors.Add(ProjectDiagnostic.In(path, $"'{name}' must be a list of strings"));
					break;
				
				case nameof(When) when condition is null:
					CheckConditions(value, path, errors);
					break;
			}
		}
	}
	
	private static bool IsStringList(TomlDocumentNode value)
	{
		if (!value.TryGetArray(out var entries))
			return false;
		
		for (var i = 0; i < entries.Count; i++)
		{
			if (value[i].ValueType != TomlValueType.String)
				return false;
		}
		
		return true;
	}
	
	private static void CheckChoice<T>(TomlDocumentNode value, string name, string place, string path,
		List<ProjectDiagnostic> errors) where T : struct, Enum
	{
		if (value.ValueType != TomlValueType.String)
			errors.Add(ProjectDiagnostic.In(path, $"'{name}'{place} must be a string"));
		else if (!Enum.GetNames<T>().Contains(value.GetString()))
			errors.Add(ProjectDiagnostic.In(path, $"'{value.GetString()}' is not a valid '{name}'{place}"));
	}
	
	private static void CheckReferences(TomlDocumentNode value, string? condition, string path,
		List<ProjectDiagnostic> errors)
	{
		var table = DescribeReferences(condition);
		if (!value.TryGetArray(out var entries))
		{
			errors.Add(ProjectDiagnostic.In(path, $"'{nameof(ProjectReferences)}' must be written as {table} tables"));
			return;
		}
		
		for (var i = 0; i < entries.Count; i++)
		{
			var entry = value[i];
			if (!entry.HasNodeOnly)
				errors.Add(ProjectDiagnostic.In(path,
					$"'{nameof(ProjectReferences)}' must be written as {table} tables"));
			else if (entry["Path"u8] is { HasValue: true, ValueType: not TomlValueType.String })
				errors.Add(ProjectDiagnostic.In(path, $"'Path' in {table} must be a string"));
			else if (entry["Alias"u8] is { HasValue: true, ValueType: not TomlValueType.String })
				errors.Add(ProjectDiagnostic.In(path, $"'Alias' in {table} must be a string"));
		}
	}
	
	private static void CheckFlags(TomlDocumentNode value, string? condition, string path,
		List<ProjectDiagnostic> errors)
	{
		var table = condition is null ? "[Flags]" : $"[When.\"{condition}\".Flags]";
		if (!value.HasNodeOnly)
		{
			errors.Add(ProjectDiagnostic.In(path, $"{table} must be a table"));
			return;
		}
		
		foreach (var (flag, setting) in value)
		{
			if (setting.ValueType != TomlValueType.Boolean)
				errors.Add(ProjectDiagnostic.In(path, $"'{flag}' in {table} must be true or false"));
		}
	}
	
	private static void CheckConditions(TomlDocumentNode value, string path, List<ProjectDiagnostic> errors)
	{
		if (!value.HasNodeOnly)
		{
			errors.Add(ProjectDiagnostic.In(path, $"'{nameof(When)}' must be a table"));
			return;
		}
		
		foreach (var (key, settings) in value)
		{
			var condition = key.ToString()!;
			if (settings.HasNodeOnly)
				CheckTypes(settings, condition, path, errors);
			else
				errors.Add(ProjectDiagnostic.In(path, $"{DescribeTable(condition)} must be a table"));
		}
	}
	
	private static string DescribeReferences(string? condition) =>
		condition is null ? "[[ProjectReferences]]" : $"[[When.\"{condition}\".ProjectReferences]]";
	
	private static void ReportUnknownSettings(TomlDocumentNode node, HashSet<string> names, string? condition,
		string path, List<ProjectDiagnostic> errors)
	{
		var place = condition is null ? "" : $" in {DescribeTable(condition)}";
		foreach (var (key, _) in node)
		{
			if (!names.Contains(key.ToString()!))
				errors.Add(ProjectDiagnostic.In(path, $"Unknown setting '{key}'{place}"));
		}
		
		var references = node["ProjectReferences"u8];
		if (!references.TryGetArray(out var entries))
			return;
		
		var table = DescribeReferences(condition);
		for (var i = 0; i < entries.Count; i++)
		{
			var hasPath = false;
			foreach (var (key, _) in references[i])
			{
				if (key.ToString() == nameof(ProjectReference.Path))
					hasPath = true;
				else if (key.ToString() != nameof(ProjectReference.Alias))
					errors.Add(ProjectDiagnostic.In(path, $"Unknown setting '{key}' in {table}"));
			}
			
			if (!hasPath)
				errors.Add(ProjectDiagnostic.In(path, $"Missing 'Path' in {table}") with { StopsChecking = true });
		}
	}
	
	public CellaProject Resolve(string path, IReadOnlyDictionary<string, bool> flags, List<ProjectDiagnostic> errors)
	{
		var matching = new List<(string Condition, ProjectSettings Settings)>();
		foreach (var (condition, settings) in When)
		{
			if (settings.OutputType is not null)
				errors.Add(ProjectDiagnostic.In(path,
					$"Cannot set '{nameof(OutputType)}' in {DescribeTable(condition)}"));
			
			if (settings.Flags is { Count: > 0 })
				errors.Add(ProjectDiagnostic.In(path, $"Cannot set '{nameof(Flags)}' in {DescribeTable(condition)}"));
			
			var tokens = new FilteredScanner(new StringSource(condition, path)).ToImmutableArray();
			var parser = new FileParser(tokens, Path.GetFileName(path), path, flags);
			if (parser.ParseCondition() == true)
				matching.Add((condition, settings));
			
			errors.AddRange(parser.Diagnostics.Select(diagnostic =>
				ProjectDiagnostic.In(path, $"{diagnostic.Message} in {DescribeTable(condition)}")));
		}
		
		var referenceable = Override(path, matching, static settings => settings.Referenceable, nameof(Referenceable),
			errors) ?? Referenceable;
		
		if (referenceable == true && !OutputType.IsExecutable())
			errors.Add(ProjectDiagnostic.In(path, $"'{nameof(Referenceable)}' only applies to executables"));
		
		return new CellaProject
		{
			OutputType = OutputType,
			CellaVersion = CellaVersion,
			StandardLibrary = StandardLibrary,
			Referenceable = referenceable,
			SystemLinkPreference = Override(path, matching, static settings => settings.SystemLinkPreference,
				nameof(SystemLinkPreference), errors) ?? SystemLinkPreference,
			AssemblyName = Override(path, matching, static settings => settings.AssemblyName, nameof(AssemblyName),
				errors) ?? AssemblyName,
			BoundsChecks = Override(path, matching, static settings => settings.BoundsChecks, nameof(BoundsChecks),
				errors) ?? BoundsChecks,
			OverflowChecks = Override(path, matching, static settings => settings.OverflowChecks,
				nameof(OverflowChecks), errors) ?? OverflowChecks,
			DebugInfo = Override(path, matching, static settings => settings.DebugInfo, nameof(DebugInfo), errors) ??
			            DebugInfo,
			ProjectReferences =
			[
				..ProjectReferences ?? [],
				..matching.SelectMany(static entry => entry.Settings.ProjectReferences ?? [])
			],
			Flags = Flags,
			Imports = Imports,
			Prelude = Prelude,
			ImportEntries = ImportEntries
		};
	}
	
	private static T? Override<T>(string path, List<(string Condition, ProjectSettings Settings)> matching,
		Func<ProjectSettings, T?> select, string name, List<ProjectDiagnostic> errors)
	{
		var setters = matching.Where(entry => select(entry.Settings) is not null).ToList();
		if (setters.Count > 1)
		{
			var tables = DescribeTables(setters.Select(static entry => entry.Condition).ToList());
			errors.Add(ProjectDiagnostic.In(path, $"'{name}' is set more than once, by {tables}"));
		}
		
		return setters.Count == 1 ? select(setters[0].Settings) : default;
	}
	
	private static bool IsFileName(string name) => name.Trim().Length > 0 && name is not ("." or "..") &&
	                                               name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
	
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
	public bool? DebugInfo { get; init; }
	
	[TomlValueOnSerialized]
	public bool? Referenceable { get; init; }
	
	[TomlValueOnSerialized]
	public List<ProjectReference>? ProjectReferences { get; init; }
	
	[TomlValueOnSerialized]
	public Dictionary<string, bool>? Flags { get; init; }
}

[TomlSerializedObject]
public sealed partial record ProjectReference
{
	[TomlValueOnSerialized]
	public string? Path { get; init; }
	
	[TomlValueOnSerialized]
	public string? Alias { get; init; }
}