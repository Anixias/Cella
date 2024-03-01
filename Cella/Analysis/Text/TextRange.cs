namespace Cella.Analysis.Text;

public readonly struct TextRange
{

	public int Start { get; }
	public int End { get; }
	public int Length => End - Start;
	public static readonly TextRange Empty = new(0, 0);
	
	public TextRange(int start, int end)
	{
		Start = start;
		End = end;
	}

	public TextRange Join(TextRange range)
	{
		return new TextRange(Math.Min(Start, range.Start), Math.Max(End, range.End));
	}

	public static TextRange operator +(TextRange left, int right)
	{
		return new TextRange(left.Start + right, left.End + right);
	}

	public static TextRange operator +(int left, TextRange right)
	{
		return new TextRange(left + right.Start, left + right.End);
	}
}