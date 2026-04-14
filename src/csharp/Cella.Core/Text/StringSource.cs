using System.Collections.Immutable;

namespace Cella.Core.Text;

public sealed class StringSource : ISource
{
	public static readonly StringSource Empty = new(string.Empty, string.Empty);
	
	public string FilePath { get; }
	public char this[int position] => _text[position];
	public int Length => _text.Length;
	
	private readonly string _text;
	private readonly ImmutableArray<TextRange> _lines;
	
	public StringSource(string text, string filePath)
	{
		_text = text;
		_lines = SplitLines(text);
		
		FilePath = filePath;
	}
	
	private static ImmutableArray<TextRange> SplitLines(string text)
	{
		var lines = new List<TextRange>();
		
		var lineStart = 0;
		for (var i = 0; i < text.Length; i++)
		{
			switch (text[i])
			{
				case '\n':
					lines.Add(new TextRange(lineStart, i));
					lineStart = i + 1;
					break;
				
				case '\r':
				{
					var lineEnd = i;
					if (i + 1 < text.Length && text[i + 1] == '\n')
					{
						i++;
					}
					
					lines.Add(new TextRange(lineStart, lineEnd));
					lineStart = i + 1;
					break;
				}
			}
		}
		
		// If the source doesn't end in a newline char, add last line
		if (lineStart < text.Length)
			lines.Add(new TextRange(lineStart, text.Length));
		
		return lines.ToImmutableArray();
	}
	
	public ReadOnlySpan<char> GetText() => _text;
	public ReadOnlySpan<char> GetText(int line) => GetText(GetLineRange(line));
	
	public ReadOnlySpan<char> GetText(TextRange range)
	{
		if (!range.IsValid || range.Start >= _text.Length || range.End > _text.Length)
			return ReadOnlySpan<char>.Empty;
		
		return _text.AsSpan().Slice(range.Start, range.Length);
	}
	
	public (int Line, int Column) GetLineColumn(int position)
	{
		if (position < 0 || position > _text.Length)
			return (0, 0);
		
		var low = 0;
		var high = _lines.Length - 1;
		
		while (low < high)
		{
			var mid = (low + high) / 2;
			if (_lines[mid].End < position)
				low = mid + 1;
			else
				high = mid;
		}
		
		var line = low + 1;
		var column = position - _lines[low].Start + 1;
		return (line, column);
	}
	
	public TextRange GetLineRange(int line)
	{
		if (line < 1 || line > _lines.Length)
			return TextRange.Empty;
		
		return _lines[line - 1];
	}
}