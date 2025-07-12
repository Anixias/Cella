namespace Cella.Core.Text;

public readonly record struct TextRange(int Start, int End)
{
	public static readonly TextRange Empty = new(0, 0);
	public static readonly TextRange EndOfFile = new(-1, 0);
	
	public int Length { get; } = End - Start;
	public bool IsValid { get; } = End >= Start && Start >= 0;
	public bool IsEmpty { get; } = End == Start;
	
	public static TextRange operator +(TextRange left, int right) => new(left.Start + right, left.End + right);
	public static TextRange operator +(int left, TextRange right) => new(left + right.Start, left + right.End);
	
	public TextRange Join(TextRange range) => new(Math.Min(Start, range.Start), Math.Max(End, range.End));
	public TextRange Join(int index) => new(Math.Min(Start, index), Math.Max(End, index));
}