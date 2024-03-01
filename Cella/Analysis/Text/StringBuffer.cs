using System.Collections.Immutable;

namespace Cella.Analysis.Text;

public sealed class StringBuffer : IBuffer
{
	public static readonly IBuffer Empty = new StringBuffer("");

	public char this[int position] => text[position];
	public int Length => text.Length;
	
	private readonly string text;
	private readonly ImmutableArray<TextRange> lines;

	public StringBuffer(string text)
	{
		this.text = text;
		lines = SplitLines(text);
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

		return lines.ToImmutableArray();
	}

	public string GetText()
	{
		return text;
	}

	public string GetText(int line)
	{
		return GetText(GetLineRange(line));
	}

	public string GetText(TextRange range)
	{
		return text.Substring(range.Start, range.Length);
	}

	public (int, int) GetLineColumn(int position)
	{
		if (position < 0 || position > text.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(position));
		}

		var line = 1;
		var column = 1;

		for (var i = 0; i < position; i++)
		{
			switch (text[i])
			{
				case '\n':
					line++;
					column = 1;
					break;
				case '\r':
				{
					if (i + 1 < text.Length && text[i + 1] == '\n')
					{
						i++;
					}

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
		return lines[line - 1];
	}
}