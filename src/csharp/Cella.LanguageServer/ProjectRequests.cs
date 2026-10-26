using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cella.Compiler.Projects;
using Cella.Diagnostics;

namespace Cella.LanguageServer;

public static class ProjectRequests
{
	private static readonly ImmutableArray<CelpKey> _topLevel = [];
	
	public static JsonArray List(IEnumerable<string> roots, Func<string, string?> textAt)
	{
		var paths = roots
			.Where(Directory.Exists)
			.SelectMany(ProjectSearch.FindProjects)
			.Select(Path.GetFullPath)
			.Distinct(Protocol.PathComparer)
			.Order(Protocol.PathComparer);
		
		var projects = new JsonArray();
		foreach (var path in paths)
		{
			if (textAt(path) is not { } text)
				continue;
			
			var document = CelpDocument.Parse(text);
			projects.Add(new JsonObject
			{
				["uri"] = Protocol.ToUri(path),
				["name"] = Path.GetFileNameWithoutExtension(path),
				["outputType"] = document.GetValue(CelpDocument.PathOf(nameof(CellaProject.OutputType))) as string,
				["referenceable"] = document.GetValue(CelpDocument.PathOf(nameof(CellaProject.Referenceable))) is true,
				["references"] = DescribeReferences(path, document)
			});
		}
		
		return projects;
	}
	
	public static JsonObject Describe(string path, string text)
	{
		var document = CelpDocument.Parse(text);
		var name = Path.GetFileNameWithoutExtension(path);
		return new JsonObject
		{
			["name"] = name,
			["errors"] =
				new JsonArray([..document.Errors.Select(JsonNode (error) => $"{error.Line}: {error.Message}")]),
			["settings"] =
				new JsonArray([..ProjectSchema.Settings.Select(JsonNode (setting) => Describe(setting, name))]),
			["values"] = DescribeValues(document, _topLevel),
			["flags"] = DescribeFlags(document),
			["lints"] = new JsonArray([
				..Lints.All.Select(JsonNode (lint) => new JsonObject
				{
					["name"] = lint.Name,
					["label"] = lint.Label,
					["default"] = lint.Default.ToString()
				})
			]),
			["lintLevels"] = DescribeLints(document, _topLevel),
			["conditions"] = new JsonArray([
				..document.Conditions.Select(JsonNode (condition) => new JsonObject
				{
					["condition"] = condition,
					["values"] = DescribeValues(document, ConditionTable(condition)),
					["lintLevels"] = DescribeLints(document, ConditionTable(condition))
				})
			]),
			["references"] = DescribeReferences(path, document)
		};
	}
	
	public static JsonArray Edit(string path, string text, JsonArray edits)
	{
		var original = CelpDocument.Parse(text);
		if (original.Errors.Count > 0)
			throw new CelpEditException($"Fix the errors in '{Path.GetFileName(path)}' first");
		
		var document = edits.OfType<JsonObject>().Aggregate(original, (current, edit) => Apply(path, current, edit));
		return document.Text == text ? [] : [Difference(text, document.Text)];
	}
	
	private static CelpDocument Apply(string path, CelpDocument document, JsonObject edit)
	{
		var condition = (string?)edit["condition"];
		var table = condition is null ? _topLevel : ConditionTable(condition);
		if ((string?)edit["setting"] is { } name)
		{
			var setting = ProjectSchema.Find(name);
			if (setting is null or
			    {
				    Kind: SettingKind.Flags or SettingKind.Lints or SettingKind.Naming or SettingKind.References
			    } ||
			    condition is not null && !setting.IsConditional)
				throw new CelpEditException($"Can't set '{name}' here");
			
			return edit["value"] is { } value
				? document.Set(table, name, ToValue(setting, value))
				: document.Remove(table, name);
		}
		
		if ((string?)edit["flag"] is { } flag)
		{
			var flags = CelpDocument.PathOf(nameof(CellaProject.Flags));
			return edit["value"] is { } value
				? document.Set(flags, flag, value.GetValue<bool>())
				: document.Remove(flags, flag);
		}
		
		if ((string?)edit["lint"] is { } lint)
		{
			if (Lints.Find(lint) is null)
				throw new CelpEditException($"Unknown lint '{lint}'");
			
			var lints = table.Add(new CelpKey(nameof(CellaProject.Lints)));
			if ((string?)edit["value"] is not { } level)
				return document.Remove(lints, lint);
			
			return Enum.TryParse<LintLevel>(level, out var parsed)
				? document.Set(lints, lint, parsed.ToString())
				: throw new CelpEditException($"'{level}' is not a valid '{lint}'");
		}
		
		if ((string?)edit["removeCondition"] is { } removed)
			return document.RemoveTable(ConditionTable(removed));
		
		if ((string?)edit["addReference"] is { } uri)
		{
			var target = Path.GetRelativePath(Path.GetDirectoryName(path)!, Protocol.ToPath(uri)).Replace('\\', '/');
			List<(string Key, object Value)> values = [(nameof(ProjectReference.Path), target)];
			if ((string?)edit["alias"] is { Length: > 0 } alias)
				values.Add((nameof(ProjectReference.Alias), alias));
			
			return document.AddTable(table, nameof(CellaProject.ProjectReferences), values);
		}
		
		if (edit["removeReference"] is { } index)
			return document.RemoveTable(ReferenceTable(table, index.GetValue<int>()));
		
		if (edit["reference"] is { } aliased)
		{
			var entry = ReferenceTable(table, aliased.GetValue<int>());
			return (string?)edit["alias"] is { Length: > 0 } alias
				? document.Set(entry, nameof(ProjectReference.Alias), alias)
				: document.Remove(entry, nameof(ProjectReference.Alias));
		}
		
		throw new CelpEditException("Unknown project change");
	}
	
	private static object ToValue(SettingInfo setting, JsonNode value)
	{
		switch (setting.Kind)
		{
			case SettingKind.Boolean when value.GetValueKind() is JsonValueKind.True
				or JsonValueKind.False:
				return value.GetValue<bool>();
			
			case SettingKind.Choice when (string?)value is { } choice && setting.Choices.Contains(choice):
				return choice;
			
			case SettingKind.Version when (string?)value is { } version && SemanticVersion.Parse(version) is not null:
				return version;
			
			case SettingKind.Text when (string?)value is { } text:
				return text;
			
			case SettingKind.TextList when value is JsonArray texts:
				return texts.Select(static text => (string?)text ?? "").ToList();
			
			default:
				throw new CelpEditException($"'{value.ToJsonString()}' is not a valid '{setting.Name}'");
		}
	}
	
	private static ImmutableArray<CelpKey> ConditionTable(string condition) =>
		CelpDocument.PathOf(nameof(CellaProject.When), condition);
	
	private static ImmutableArray<CelpKey> ReferenceTable(ImmutableArray<CelpKey> table, int index) =>
		table.Add(new CelpKey(nameof(CellaProject.ProjectReferences), index));
	
	private static JsonObject Describe(SettingInfo setting, string projectName) => new()
	{
		["name"] = setting.Name,
		["label"] = setting.Label,
		["kind"] = CamelCase(setting.Kind.ToString()),
		["conditional"] = setting.IsConditional,
		["default"] = setting.DefaultsToProjectName ? projectName : setting.Default,
		["choices"] = new JsonArray([..setting.Choices.Select(static choice => (JsonNode)choice)])
	};
	
	private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
	
	private static JsonObject DescribeValues(CelpDocument document, ImmutableArray<CelpKey> table)
	{
		var values = new JsonObject();
		foreach (var setting in ProjectSchema.Settings.Where(static setting =>
			         setting.Kind is not (SettingKind.Flags or SettingKind.Lints or SettingKind.Naming
				         or SettingKind.References)))
		{
			var value = document.GetValue(table.Add(new CelpKey(setting.Name)));
			values[setting.Name] = value switch
			{
				bool flag => flag,
				string text => text,
				List<object?> elements => new JsonArray([
					..elements.Select(static element => (JsonNode?)(element as string))
				]),
				_ => null
			};
			
			if (values[setting.Name] is null)
				values.Remove(setting.Name);
		}
		
		return values;
	}
	
	private static JsonObject DescribeLints(CelpDocument document, ImmutableArray<CelpKey> table)
	{
		var lints = table.Add(new CelpKey(nameof(CellaProject.Lints)));
		var levels = new JsonObject();
		foreach (var lint in document.GetKeys(lints))
		{
			if (document.GetValue(lints.Add(new CelpKey(lint))) is string level)
				levels[lint] = level;
		}
		
		return levels;
	}
	
	private static JsonObject DescribeFlags(CelpDocument document)
	{
		var table = CelpDocument.PathOf(nameof(CellaProject.Flags));
		var flags = new JsonObject();
		foreach (var flag in document.GetKeys(table))
		{
			if (document.GetValue(table.Add(new CelpKey(flag))) is bool value)
				flags[flag] = value;
		}
		
		return flags;
	}
	
	private static JsonArray DescribeReferences(string path, CelpDocument document)
	{
		var directory = Path.GetDirectoryName(path)!;
		var references = new JsonArray();
		foreach (var condition in new string?[] { null }.Concat(document.Conditions))
		{
			var table = condition is null ? _topLevel : ConditionTable(condition);
			foreach (var reference in document.GetReferences(table))
			{
				var target = Path.GetFullPath(Path.Combine(directory, reference.Path));
				references.Add(new JsonObject
				{
					["uri"] = Protocol.ToUri(target),
					["path"] = reference.Path,
					["alias"] = reference.Alias,
					["condition"] = condition,
					["index"] = reference.Index,
					["exists"] = File.Exists(target)
				});
			}
		}
		
		return references;
	}
	
	private static JsonObject Difference(string before, string after)
	{
		var prefix = 0;
		var limit = Math.Min(before.Length, after.Length);
		while (prefix < limit && before[prefix] == after[prefix])
			prefix++;
		
		var suffix = 0;
		while (suffix < limit - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
			suffix++;
		
		return new JsonObject
		{
			["range"] = new JsonObject
			{
				["start"] = PositionOf(before, prefix),
				["end"] = PositionOf(before, before.Length - suffix)
			},
			["newText"] = after[prefix..^suffix]
		};
	}
	
	private static JsonObject PositionOf(string text, int offset)
	{
		var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
		var line = 0;
		for (var i = 0; i < lineStart; i++)
		{
			if (text[i] == '\n')
				line++;
		}
		
		return new JsonObject { ["line"] = line, ["character"] = offset - lineStart };
	}
}