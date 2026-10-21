namespace Cella.Core.Text;

public static class SourceLayout
{
	public static string FindNewline(this ISource source)
	{
		for (var i = 0; i < source.Length; i++)
		{
			if (source[i] == '\n')
				return i > 0 && source[i - 1] == '\r' ? "\r\n" : "\n";
		}
		
		return "\n";
	}
	
	public static int FindLineEnd(this ISource source, int position)
	{
		while (position < source.Length && source[position] is not ('\r' or '\n'))
			position++;
		
		return position;
	}
	
	public static int FindLineStart(this ISource source, int position)
	{
		while (position > 0 && source[position - 1] != '\n')
			position--;
		
		return position;
	}
	
	public static string FindIndent(this ISource source, int position)
	{
		var start = source.FindLineStart(position);
		var end = start;
		while (end < source.Length && source[end] is ' ' or '\t')
			end++;
		
		return source.GetText(new(start, end)).ToString();
	}
}