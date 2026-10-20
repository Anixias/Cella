using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cella.Compiler.Projects;

public sealed record CelpEntry(string Setting, string Value, int Start, int End, bool IsExact);
public readonly record struct CelpKey(string Name, int? Index = null);

public enum CelpItemKind
{
	Value,
	Table,
	ArrayTable
}

public sealed record CelpItem
(
	ImmutableArray<CelpKey> Path,
	CelpItemKind Kind,
	int LineStart,
	int KeyStart,
	int Start,
	int End,
	object? Value,
	bool IsInline
);

public sealed partial class CelpReader
{
	private const char Separator = '\u001F';
	
	private readonly string _text;
	private readonly List<int> _lineStarts = [0];
	private readonly Dictionary<string, List<(int Line, string Name)>> _keys = [];
	private readonly Dictionary<string, List<(int Line, string Name)>> _tables = [];
	private readonly Dictionary<string, int> _arrays = [];
	private int position;
	private int lastKeyStart;
	
	private CelpReader(string text)
	{
		_text = text;
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] == '\n')
				_lineStarts.Add(i + 1);
		}
	}
	
	public List<(int Line, string Message)> Errors { get; } = [];
	public List<CelpEntry> ImportEntries { get; } = [];
	public List<CelpItem> Items { get; } = [];
	
	public static CelpReader Read(string text)
	{
		var reader = new CelpReader(text);
		try
		{
			reader.ReadDocument();
			reader.ReportDuplicates();
		}
		catch (CelpException e)
		{
			reader.Errors.Add((reader.GetLine(e.Position), e.Message));
		}
		
		return reader;
	}
	
	private sealed class CelpException(int position, string message) : Exception(message)
	{
		public int Position { get; } = position;
	}
	
	private sealed record StringValue(string Value, int Start, int End, bool IsExact);
	
	private int GetLine(int offset)
	{
		var index = _lineStarts.BinarySearch(offset);
		return (index >= 0 ? index : ~index - 1) + 1;
	}
	
	private int GetLineStart(int offset) => _lineStarts[GetLine(offset) - 1];
	
	private static object? ToItemValue(object? value) => value switch
	{
		StringValue text => text.Value,
		List<object?> elements => elements.Select(ToItemValue).ToList(),
		_ => value
	};
	
	private bool AtEnd => position >= _text.Length;
	
	private char Current => _text[position];
	
	private bool Match(string expected)
	{
		if (string.CompareOrdinal(_text, position, expected, 0, expected.Length) != 0)
			return false;
		
		position += expected.Length;
		return true;
	}
	
	private bool IsLineEnd => AtEnd || Current == '\n' || Current == '\r' && position + 1 < _text.Length &&
		_text[position + 1] == '\n';
	
	private void SkipSpaces()
	{
		while (!AtEnd && Current is ' ' or '\t')
			position++;
	}
	
	private void SkipComment()
	{
		if (AtEnd || Current != '#')
			return;
		
		while (!IsLineEnd)
			position++;
	}
	
	private void SkipLineEnd() => _ = Match("\r\n") || Match("\n");
	
	private void SkipBlank()
	{
		while (true)
		{
			SkipSpaces();
			SkipComment();
			if (AtEnd || !IsLineEnd)
				return;
			
			SkipLineEnd();
		}
	}
	
	private void ReadDocument()
	{
		if (!AtEnd && Current == '﻿')
			position++;
		
		List<CelpKey> table = [];
		while (true)
		{
			SkipBlank();
			if (AtEnd)
				return;
			
			if (Current == '[')
				table = ReadHeader();
			else
				ReadKeyValue(table);
			
			SkipSpaces();
			SkipComment();
			if (!IsLineEnd)
				throw new CelpException(position, "Expected a new line after the value");
			
			SkipLineEnd();
		}
	}
	
	private List<CelpKey> ReadHeader()
	{
		var start = position;
		var isArray = Match("[[");
		if (!isArray)
			position++;
		
		SkipSpaces();
		var segments = ReadKey();
		SkipSpaces();
		if (!Match(isArray ? "]]" : "]"))
			throw new CelpException(position, isArray ? "Expected ']]'" : "Expected ']'");
		
		if (!isArray)
		{
			var path = Resolve(segments);
			_tables.GetOrAdd(Join(path)).Add((GetLine(start), _text[start..position]));
			Items.Add(new CelpItem([..path], CelpItemKind.Table, GetLineStart(start), start, start, position, null,
				false));
			
			return path;
		}
		
		var parent = Resolve([..segments.Take(segments.Count - 1)]);
		var key = Join([..parent, new CelpKey(segments[^1])]);
		var index = _arrays.GetValueOrDefault(key);
		_arrays[key] = index + 1;
		List<CelpKey> entry = [..parent, new CelpKey(segments[^1], index)];
		Items.Add(new CelpItem([..entry], CelpItemKind.ArrayTable, GetLineStart(start), start, start, position, null,
			false));
		
		return entry;
	}
	
	private List<CelpKey> Resolve(List<string> segments)
	{
		var resolved = new List<CelpKey>();
		foreach (var segment in segments)
		{
			var key = Join([..resolved, new CelpKey(segment)]);
			resolved.Add(new CelpKey(segment, _arrays.TryGetValue(key, out var count) ? count - 1 : null));
		}
		
		return resolved;
	}
	
	private static string Join(IEnumerable<CelpKey> path) => string.Join(Separator,
		path.Select(static key => key.Index is { } index ? $"{key.Name}[{index}]" : key.Name));
	
	private void ReadKeyValue(List<CelpKey> table)
	{
		var start = position;
		var segments = ReadKey();
		var written = _text[start..position].Trim();
		var keyStart = lastKeyStart;
		SkipSpaces();
		if (!Match("="))
			throw new CelpException(position, "Expected '='");
		
		SkipSpaces();
		List<CelpKey> path = [..table, ..segments.Select(static segment => new CelpKey(segment))];
		_keys.GetOrAdd(Join(path)).Add((GetLine(start), written));
		var valueStart = position;
		var value = ReadValue(path);
		Items.Add(new CelpItem([..path], CelpItemKind.Value, GetLineStart(start), keyStart, valueStart, position,
			ToItemValue(value), false));
		
		if (table.Count == 0 && segments is
			    [var setting and (nameof(CellaProject.Imports) or nameof(CellaProject.Prelude))] &&
		    value is List<object?> elements && elements.All(static element => element is StringValue))
			ImportEntries.AddRange(elements.Cast<StringValue>()
				.Select(entry => new CelpEntry(setting, entry.Value, entry.Start, entry.End, entry.IsExact)));
	}
	
	private List<string> ReadKey()
	{
		var segments = new List<string>();
		while (true)
		{
			SkipSpaces();
			lastKeyStart = position;
			segments.Add(ReadSimpleKey());
			SkipSpaces();
			if (AtEnd || Current != '.')
				return segments;
			
			position++;
		}
	}
	
	private string ReadSimpleKey()
	{
		if (!AtEnd && Current == '"')
			return ReadBasicString().Value;
		
		if (!AtEnd && Current == '\'')
			return ReadLiteralString().Value;
		
		var start = position;
		while (!AtEnd && (char.IsAsciiLetterOrDigit(Current) || Current is '_' or '-'))
			position++;
		
		if (position == start)
			throw new CelpException(position, "Expected a key");
		
		return _text[start..position];
	}
	
	private object? ReadValue(List<CelpKey> path)
	{
		if (AtEnd || IsLineEnd)
			throw new CelpException(position, "Expected a value");
		
		switch (Current)
		{
			case '"':
				return _text.AsSpan(position).StartsWith("\"\"\"") ? ReadMultilineBasicString() : ReadBasicString();
			
			case '\'':
				return _text.AsSpan(position).StartsWith("'''") ? ReadMultilineLiteralString() : ReadLiteralString();
			
			case '[':
				return ReadArray(path);
			
			case '{':
				ReadInlineTable(path);
				return null;
		}
		
		var start = position;
		while (!AtEnd && !IsLineEnd && Current is not (' ' or '\t' or ',' or ']' or '}' or '#'))
			position++;
		
		var word = _text[start..position];
		if (word is "true" or "false")
			return word == "true";
		
		if (ValuePattern().IsMatch(word))
			return null;
		
		throw new CelpException(start, "Expected a value");
	}
	
	[GeneratedRegex(
		"""^([+-]?(0|[1-9](_?[0-9])*)|0x[0-9A-Fa-f](_?[0-9A-Fa-f])*|0o[0-7](_?[0-7])*|0b[01](_?[01])*|[+-]?(0|[1-9](_?[0-9])*)(\.[0-9](_?[0-9])*)?([eE][+-]?[0-9](_?[0-9])*)?|[+-]?(inf|nan)|[0-9]{4}-[0-9]{2}-[0-9]{2}([Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|z|[+-][0-9]{2}:[0-9]{2})?)?|[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?)$""")]
	private static partial Regex ValuePattern();
	
	private List<object?> ReadArray(List<CelpKey> path)
	{
		position++;
		var elements = new List<object?>();
		while (true)
		{
			SkipBlank();
			if (Match("]"))
				return elements;
			
			elements.Add(ReadValue(path[^1].Index is null
				? [..path.Take(path.Count - 1), path[^1] with { Index = elements.Count }]
				: [..path, new CelpKey("", elements.Count)]));
			
			SkipBlank();
			if (Match(","))
				continue;
			
			if (Match("]"))
				return elements;
			
			throw new CelpException(position, "Expected ',' or ']'");
		}
	}
	
	private void ReadInlineTable(List<CelpKey> path)
	{
		position++;
		SkipSpaces();
		if (Match("}"))
			return;
		
		while (true)
		{
			SkipSpaces();
			if (IsLineEnd)
				throw new CelpException(position, "Expected '}'");
			
			var start = position;
			var segments = ReadKey();
			var written = _text[start..position].Trim();
			var keyStart = lastKeyStart;
			SkipSpaces();
			if (!Match("="))
				throw new CelpException(position, "Expected '='");
			
			SkipSpaces();
			List<CelpKey> key = [..path, ..segments.Select(static segment => new CelpKey(segment))];
			_keys.GetOrAdd(Join(key)).Add((GetLine(start), written));
			var valueStart = position;
			var value = ReadValue(key);
			Items.Add(new CelpItem([..key], CelpItemKind.Value, GetLineStart(start), keyStart, valueStart, position,
				ToItemValue(value), true));
			
			SkipSpaces();
			if (Match(","))
				continue;
			
			if (Match("}"))
				return;
			
			throw new CelpException(position, "Expected ',' or '}'");
		}
	}
	
	private StringValue ReadBasicString()
	{
		var opening = position++;
		var start = position;
		var builder = new StringBuilder();
		var isExact = true;
		while (true)
		{
			if (IsLineEnd)
				throw new CelpException(opening, "Unterminated string");
			
			var character = Current;
			if (character == '"')
				return new StringValue(builder.ToString(), start, position++, isExact);
			
			if (character == '\\')
			{
				isExact = false;
				ReadEscape(builder);
				continue;
			}
			
			builder.Append(character);
			position++;
		}
	}
	
	private StringValue ReadMultilineBasicString()
	{
		var opening = position;
		position += 3;
		SkipLineEnd();
		var start = position;
		var builder = new StringBuilder();
		while (true)
		{
			if (AtEnd)
				throw new CelpException(opening, "Unterminated string");
			
			if (Match("\"\"\""))
			{
				while (!AtEnd && Current == '"')
				{
					builder.Append('"');
					position++;
				}
				
				return new StringValue(builder.ToString(), start, position - 3, false);
			}
			
			if (Current == '\\' && IsLineContinuation())
			{
				position++;
				while (!AtEnd && char.IsWhiteSpace(Current))
					position++;
				
				continue;
			}
			
			if (Current == '\\')
			{
				ReadEscape(builder);
				continue;
			}
			
			builder.Append(Current);
			position++;
		}
	}
	
	private bool IsLineContinuation()
	{
		var next = position + 1;
		while (next < _text.Length && _text[next] is ' ' or '\t')
			next++;
		
		return next < _text.Length && _text[next] is '\n' or '\r';
	}
	
	private void ReadEscape(StringBuilder builder)
	{
		var escape = position++;
		if (AtEnd)
			throw new CelpException(escape, "Invalid escape sequence");
		
		var kind = Current;
		position++;
		switch (kind)
		{
			case 'b':
				builder.Append('\b');
				return;
			
			case 't':
				builder.Append('\t');
				return;
			
			case 'n':
				builder.Append('\n');
				return;
			
			case 'f':
				builder.Append('\f');
				return;
			
			case 'r':
				builder.Append('\r');
				return;
			
			case 'e':
				builder.Append('\u001B');
				return;
			
			case '"':
				builder.Append('"');
				return;
			
			case '\\':
				builder.Append('\\');
				return;
			
			case 'u' or 'U':
				var length = kind == 'u' ? 4 : 8;
				if (position + length > _text.Length || !int.TryParse(_text.AsSpan(position, length),
					    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) ||
				    !Rune.IsValid(code))
					throw new CelpException(escape, "Invalid escape sequence");
				
				builder.Append(new Rune(code).ToString());
				position += length;
				return;
			
			default:
				throw new CelpException(escape, "Invalid escape sequence");
		}
	}
	
	private StringValue ReadLiteralString()
	{
		var opening = position++;
		var start = position;
		while (true)
		{
			if (IsLineEnd)
				throw new CelpException(opening, "Unterminated string");
			
			if (Current == '\'')
				return new StringValue(_text[start..position], start, position++, true);
			
			position++;
		}
	}
	
	private StringValue ReadMultilineLiteralString()
	{
		var opening = position;
		position += 3;
		SkipLineEnd();
		var start = position;
		var end = _text.IndexOf("'''", position, StringComparison.Ordinal);
		if (end < 0)
			throw new CelpException(opening, "Unterminated string");
		
		while (end + 3 < _text.Length && _text[end + 3] == '\'')
			end++;
		
		position = end + 3;
		return new StringValue(_text[start..end], start, end, false);
	}
	
	private void ReportDuplicates()
	{
		var duplicates = new List<(int Line, string Message)>();
		foreach (var occurrences in _tables.Values.Where(static list => list.Count > 1))
			duplicates.AddRange(occurrences.DistinctBy(static occurrence => occurrence.Line)
				.Select(static occurrence => (occurrence.Line, $"'{occurrence.Name}' appears more than once")));
		
		foreach (var occurrences in _keys.Values.Where(static list => list.Count > 1))
			duplicates.AddRange(occurrences.DistinctBy(static occurrence => occurrence.Line)
				.Select(static occurrence => (occurrence.Line, $"'{occurrence.Name}' is set more than once")));
		
		Errors.AddRange(duplicates.OrderBy(static duplicate => duplicate.Line));
	}
}