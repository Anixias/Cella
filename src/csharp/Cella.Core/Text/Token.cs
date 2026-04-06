namespace Cella.Core.Text;

public readonly record struct Token
{
	public static Token EndOfFile(ISource source) => new(TokenType.EndOfFile, new(source, TextRange.EndOfFile));
	
	public TokenType Type { get; init; }
	public SourceLocation SourceLocation { get; init; }
	public int Line { get; }
	public string Text { get; }
	public ReadOnlySpan<char> AsSpan() => Text.AsSpan();
	public ReadOnlySpan<char> AsSourceSpan() => SourceLocation.GetText();
	
	public Token(TokenType type, ISource source, TextRange textRange, string? text = null)
		: this(type, new(source, textRange), text)
	{
	}
	
	public Token(TokenType type, SourceLocation sourceLocation, string? text = null)
	{
		Type = type;
		SourceLocation = sourceLocation;
		Line = sourceLocation.Source.GetLineColumn(sourceLocation.Range.Start).Line;
		Text = text ?? new(SourceLocation.GetText());
	}
	
	public override string ToString() => $"{Type} [{AsSpan()}]";
}