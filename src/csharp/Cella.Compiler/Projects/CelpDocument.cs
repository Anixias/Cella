using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cella.Compiler.Projects;

public sealed record CelpReference(int Index, string Path, string? Alias);
public sealed class CelpEditException(string message) : Exception(message);

public sealed partial class CelpDocument
{
	private readonly CelpReader _reader;
	private readonly string _newLine;
	
	private CelpDocument(string text)
	{
		Text = text;
		_reader = CelpReader.Read(text);
		_newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
	}
	
	public string Text { get; }
	
	public IReadOnlyList<(int Line, string Message)> Errors => _reader.Errors;
	
	public static CelpDocument Parse(string text) => new(text);
	
	public static ImmutableArray<CelpKey> PathOf(params IEnumerable<string> names) =>
		[..names.Select(static name => new CelpKey(name))];
	
	public object? GetValue(ImmutableArray<CelpKey> path) => FindValue(path)?.Value;
	
	public IReadOnlyList<string> Conditions => _reader.Items
		.Where(static item => item.Path is [{ Name: nameof(CellaProject.When), Index: null }, { Index: null }, ..])
		.Select(static item => item.Path[1].Name)
		.Distinct()
		.ToList();
	
	public IReadOnlyList<string> GetKeys(ImmutableArray<CelpKey> table) => _reader.Items
		.Where(item => item.Kind == CelpItemKind.Value && item.Path.Length == table.Length + 1 &&
		               StartsWith(item.Path, table))
		.Select(static item => item.Path[^1].Name)
		.Distinct()
		.ToList();
	
	public IReadOnlyList<CelpReference> GetReferences(ImmutableArray<CelpKey> table)
	{
		var entries = _reader.Items
			.Where(item => item.Path.Length > table.Length && StartsWith(item.Path, table) &&
			               item.Path[table.Length] is { Name: nameof(CellaProject.ProjectReferences), Index: not null })
			.Select(item => item.Path[..(table.Length + 1)])
			.DistinctBy(static path => path[^1].Index)
			.OrderBy(static path => path[^1].Index);
		
		var references = new List<CelpReference>();
		foreach (var entry in entries)
		{
			if (GetValue(entry.Add(new CelpKey(nameof(ProjectReference.Path)))) is string path)
				references.Add(new CelpReference(entry[^1].Index!.Value, path,
					GetValue(entry.Add(new CelpKey(nameof(ProjectReference.Alias)))) as string));
		}
		
		return references;
	}
	
	public CelpDocument Set(ImmutableArray<CelpKey> table, string key, object value)
	{
		var path = table.Add(new CelpKey(key));
		var line = $"{FormatKey(key)} = {Format(key, value)}";
		if (FindValue(path) is { } existing)
			return Replace(existing.Start, existing.End, Format(key, value), path);
		
		if (IsInline(table))
			throw Unsupported(path);
		
		if (table.IsEmpty || FindHeader(table) is not null)
		{
			var header = table.IsEmpty ? null : FindHeader(table);
			var sectionStart = header is null ? 0 : LineEnd(header.End);
			var sectionEnd = _reader.Items
				.Where(item => item.Kind != CelpItemKind.Value && item.LineStart >= sectionStart)
				.Select(static item => item.LineStart)
				.DefaultIfEmpty(Text.Length)
				.Min();
			
			var last = _reader.Items.LastOrDefault(item => item is { Kind: CelpItemKind.Value, IsInline: false } &&
			                                               item.LineStart >= sectionStart &&
			                                               item.LineStart < sectionEnd);
			
			return InsertLine(last is null ? sectionStart : LineEnd(last.End), line, path);
		}
		
		if (_reader.Items.LastOrDefault(item => item is { Kind: CelpItemKind.Value, IsInline: false } &&
		                                        item.Path.Length == path.Length && StartsWith(item.Path, table)) is
		    { } sibling)
			return InsertLine(LineEnd(sibling.End), Text[sibling.LineStart..sibling.KeyStart] + line, path);
		
		if (_reader.Items.Any(item => item.Kind == CelpItemKind.Value && StartsWith(item.Path, table)))
			throw Unsupported(path);
		
		return AppendSection($"[{FormatPath(table)}]", [line], path);
	}
	
	public CelpDocument Remove(ImmutableArray<CelpKey> table, string key)
	{
		var path = table.Add(new CelpKey(key));
		if (FindValue(path) is not { } item)
			return this;
		
		if (item.IsInline)
			throw Unsupported(path);
		
		return Replace(item.LineStart, LineEnd(item.End), "", path);
	}
	
	public CelpDocument RemoveTable(ImmutableArray<CelpKey> table)
	{
		var items = _reader.Items;
		var ranges = new List<(int Start, int End)>();
		foreach (var item in items.Where(item => StartsWith(item.Path, table)))
		{
			if (item.Kind != CelpItemKind.Value)
			{
				var next = items.FirstOrDefault(other => other.Kind != CelpItemKind.Value &&
				                                         other.LineStart > item.LineStart);
				
				ranges.Add(next is null
					? (SkipBlankLinesBack(item.LineStart), Text.Length)
					: (item.LineStart, next.LineStart));
			}
			else if (!item.IsInline)
				ranges.Add((item.LineStart, LineEnd(item.End)));
		}
		
		if (items.Any(item => item.IsInline && StartsWith(item.Path, table) &&
		                      !ranges.Any(range => item.LineStart >= range.Start && item.LineStart < range.End)))
			throw Unsupported(table);
		
		if (ranges.Count == 0)
			return this;
		
		var text = new StringBuilder();
		var copied = 0;
		foreach (var (start, end) in ranges.OrderBy(static range => range.Start))
		{
			if (start > copied)
				text.Append(Text, copied, start - copied);
			
			copied = Math.Max(copied, end);
		}
		
		text.Append(Text, copied, Text.Length - copied);
		if (copied < Text.Length)
			return Change(text.ToString(), table);
		
		var kept = text.ToString().TrimEnd();
		return Change(kept.Length > 0 ? kept + _newLine : "", table);
	}
	
	public CelpDocument AddTable(ImmutableArray<CelpKey> table, string key,
		IReadOnlyList<(string Key, object Value)> values)
	{
		var array = table.Add(new CelpKey(key));
		if (FindValue(array) is not null || IsInline(table))
			throw Unsupported(array);
		
		var lines = values.Select(value => $"{FormatKey(value.Key)} = {Format(value.Key, value.Value)}").ToList();
		var header = $"[[{FormatPath(array)}]]";
		var last = _reader.Items.LastOrDefault(item => item.Kind == CelpItemKind.ArrayTable &&
		                                               item.Path.Length == array.Length &&
		                                               StartsWith(item.Path, table) && item.Path[^1].Name == key);
		
		var next = last is null
			? null
			: _reader.Items.FirstOrDefault(item => item.Kind != CelpItemKind.Value && item.LineStart > last.LineStart);
		
		if (next is null)
			return AppendSection(header, lines, array);
		
		var section = new StringBuilder(header).Append(_newLine);
		foreach (var line in lines)
			section.Append(line).Append(_newLine);
		
		return Replace(next.LineStart, next.LineStart, section.Append(_newLine).ToString(), array);
	}
	
	private CelpItem? FindValue(ImmutableArray<CelpKey> path) =>
		_reader.Items.FirstOrDefault(item => item.Kind == CelpItemKind.Value && item.Path.SequenceEqual(path));
	
	private CelpItem? FindHeader(ImmutableArray<CelpKey> table) =>
		_reader.Items.FirstOrDefault(item => item.Kind != CelpItemKind.Value && item.Path.SequenceEqual(table));
	
	private bool IsInline(ImmutableArray<CelpKey> table) => !table.IsEmpty &&
	                                                        (FindValue(table) is not null ||
	                                                         _reader.Items.Any(item =>
		                                                         item.IsInline && StartsWith(item.Path, table)));
	
	private static bool StartsWith(ImmutableArray<CelpKey> path, ImmutableArray<CelpKey> prefix) =>
		path.Length >= prefix.Length && path.AsSpan(0, prefix.Length).SequenceEqual(prefix.AsSpan());
	
	private int LineEnd(int offset)
	{
		var end = Text.IndexOf('\n', Math.Min(offset, Text.Length));
		return end < 0 ? Text.Length : end + 1;
	}
	
	private int SkipBlankLinesBack(int lineStart)
	{
		while (lineStart > 0)
		{
			var previous = lineStart >= 2 ? Text.LastIndexOf('\n', lineStart - 2) + 1 : 0;
			if (!string.IsNullOrWhiteSpace(Text[previous..lineStart]))
				return lineStart;
			
			lineStart = previous;
		}
		
		return 0;
	}
	
	private CelpDocument InsertLine(int offset, string line, ImmutableArray<CelpKey> path)
	{
		var prefix = offset == Text.Length && Text.Length > 0 && !Text.EndsWith('\n') ? _newLine : "";
		return Replace(offset, offset, prefix + line + _newLine, path);
	}
	
	private CelpDocument AppendSection(string header, IEnumerable<string> lines, ImmutableArray<CelpKey> path)
	{
		var section = new StringBuilder();
		if (Text.Length > 0)
			section.Append(Text.EndsWith('\n') ? "" : _newLine).Append(_newLine);
		
		section.Append(header).Append(_newLine);
		foreach (var line in lines)
			section.Append(line).Append(_newLine);
		
		return Replace(Text.Length, Text.Length, section.ToString(), path);
	}
	
	private CelpDocument Replace(int start, int end, string text, ImmutableArray<CelpKey> path) =>
		Change(string.Concat(Text.AsSpan(0, start), text, Text.AsSpan(end)), path);
	
	private CelpDocument Change(string text, ImmutableArray<CelpKey> path)
	{
		var changed = new CelpDocument(text);
		if (changed.Errors.Count > 0)
			throw Unsupported(path);
		
		return changed;
	}
	
	private static CelpEditException Unsupported(ImmutableArray<CelpKey> path) =>
		new($"Can't change '{FormatPath(path)}' in this file");
	
	private static string FormatPath(ImmutableArray<CelpKey> path) =>
		string.Join('.', path.Select(static key => FormatKey(key.Name)));
	
	private static string FormatKey(string name) => BareKey().IsMatch(name) ? name : Quote(name);
	
	private static string Format(string key, object value) => value switch
	{
		bool flag => flag ? "true" : "false",
		string text when key == nameof(ProjectReference.Path) && IsLiteral(text) => $"'{text}'",
		string text => Quote(text),
		IEnumerable<string> texts => $"[{string.Join(", ", texts.Select(Quote))}]",
		_ => throw new ArgumentException($"Can't write a {value.GetType().Name}", nameof(value))
	};
	
	private static bool IsLiteral(string text) => !text.Any(static character => character == '\'' ||
		char.IsControl(character));
	
	private static string Quote(string text)
	{
		var builder = new StringBuilder("\"");
		foreach (var character in text)
		{
			builder.Append(character switch
			{
				'"' => "\\\"",
				'\\' => "\\\\",
				'\b' => "\\b",
				'\t' => "\\t",
				'\n' => "\\n",
				'\f' => "\\f",
				'\r' => "\\r",
				_ when char.IsControl(character) => $"\\u{(int)character:X4}",
				_ => character.ToString(CultureInfo.InvariantCulture)
			});
		}
		
		return builder.Append('"').ToString();
	}
	
	[GeneratedRegex("^[A-Za-z0-9_-]+$")]
	private static partial Regex BareKey();
}