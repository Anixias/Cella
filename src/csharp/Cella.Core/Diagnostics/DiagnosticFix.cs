using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Diagnostics;

public sealed record TextEdit(SourceLocation Location, string NewText);

public sealed record DiagnosticFix(string Title, ImmutableArray<TextEdit> Edits)
{
	public DiagnosticFix(string title, params TextEdit[] edits) : this(title, ImmutableArray.Create(edits))
	{
	}
	
	public static DiagnosticFix Replace(string title, SourceLocation location, string text) =>
		new(title, new TextEdit(location, text));
	
	public static DiagnosticFix RemoveRedundant(SourceLocation location) =>
		Remove($"Remove redundant '{location.GetText()}'", location);
	
	public static Diagnostic Redundant(SourceLocation location) =>
		new(DiagnosticSeverity.Hint, location, $"Redundant '{location.GetText()}'")
		{
			Fixes = [RemoveRedundant(location)],
			Lint = Lints.Redundant
		};
	
	public static DiagnosticFix? RemoveRedundantBound(SourceLocation location)
	{
		var title = $"Remove redundant '{location.GetText()}'";
		return IsNextTo(location, '+') ? RemoveListItem(title, location, '+') : null;
	}
	
	public static DiagnosticFix RemoveExactly(string title, SourceLocation location) =>
		new(title, new TextEdit(location, ""));
	
	private static bool IsNextTo(SourceLocation location, char separator)
	{
		var (source, range) = location;
		var before = range.Start;
		while (before > 0 && source[before - 1] is ' ' or '\t')
			before--;
		
		var after = range.End;
		while (after < source.Length && source[after] is ' ' or '\t')
			after++;
		
		return before > 0 && source[before - 1] == separator || after < source.Length && source[after] == separator;
	}
	
	public static DiagnosticFix Remove(string title, SourceLocation location)
	{
		var (source, range) = location;
		var end = range.End;
		while (end < source.Length && source[end] is ' ' or '\t')
			end++;
		
		if (end < source.Length && source[end] is '\r' or '\n' || end >= source.Length)
		{
			var start = range.Start;
			while (start > 0 && source[start - 1] is ' ' or '\t')
				start--;
			
			end = start > 0 && source[start - 1] != '\n' ? end : SkipLineBreak(source, end);
			return new(title, new TextEdit(new(source, new(start, end)), ""));
		}
		
		return new(title, new TextEdit(new(source, new(range.Start, end)), ""));
	}
	
	public static DiagnosticFix RemoveListItem(string title, SourceLocation location, char separator)
	{
		var (source, range) = location;
		var before = range.Start;
		while (before > 0 && source[before - 1] is ' ' or '\t')
			before--;
		
		if (before > 0 && source[before - 1] == separator)
		{
			var start = before - 1;
			while (start > 0 && source[start - 1] is ' ' or '\t')
				start--;
			
			return new(title, new TextEdit(new(source, new(start, range.End)), ""));
		}
		
		var after = range.End;
		while (after < source.Length && source[after] is ' ' or '\t')
			after++;
		
		if (after < source.Length && source[after] == separator)
		{
			after++;
			while (after < source.Length && source[after] is ' ' or '\t')
				after++;
			
			return new(title, new TextEdit(new(source, new(range.Start, after)), ""));
		}
		
		return Remove(title, location);
	}
	
	private static int SkipLineBreak(ISource source, int position)
	{
		if (position < source.Length && source[position] == '\r')
			position++;
		
		if (position < source.Length && source[position] == '\n')
			position++;
		
		return position;
	}
}