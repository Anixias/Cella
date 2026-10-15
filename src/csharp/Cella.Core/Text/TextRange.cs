namespace Cella.Core.Text;

public readonly record struct TextRange(int Start, int End)
{
	public static readonly TextRange Empty = new(0, 0);
	public static readonly TextRange EndOfFile = new(-1, 0);
	
	public int Length { get; } = End - Start;
	public bool IsValid { get; } = End >= Start && Start >= 0;
	
	public TextRange Join(TextRange range) => new(Math.Min(Start, range.Start), Math.Max(End, range.End));
}