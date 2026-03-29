using System.Collections.Immutable;

namespace Cella.Core.Text;

public sealed class StringSource : ISource
{
	public static readonly StringSource Empty = new(string.Empty);
	
	public char this[int position] => _text[position];
	public int Length => _text.Length;
	
	private readonly string _text;
	private readonly ImmutableArray<TextRange> _lines;
	
	public StringSource(string text)
	{
		_text = text;
		_lines = SplitLines(text);
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
	
	public (int line, int column) GetLineColumn(int position)
	{
		if (position < 0 || position > _text.Length)
			throw new ArgumentOutOfRangeException(nameof(position));
		
		var line = 1;
		var column = 1;
		
		for (var i = 0; i < position; i++)
		{
			switch (_text[i])
			{
				case '\n':
					line++;
					column = 1;
					break;
				
				case '\r':
				{
					if (i + 1 < _text.Length && _text[i + 1] == '\n')
						i++;
					
					line++;
					column = 1;
					break;
				}
				
				default:
					column++;
					break;
			}
		}
		
		return (line, column);
	}
	
	public TextRange GetLineRange(int line)
	{
		if (line < 1 || line > _lines.Length)
			return TextRange.Empty;
		
		return _lines[line - 1];
	}
}