namespace Cella.Core.Text;

public readonly record struct Token
{
	public static Token EndOfFile(ISource source) => new(TokenType.EndOfFile, new(source, TextRange.EndOfFile));
	
	public TokenType Type { get; init; }
	public SourceLocation SourceLocation { get; init; }
	public int Line { get; }
	public string GetText() => new(AsSpan());
	public ReadOnlySpan<char> AsSpan() => SourceLocation.GetText();
	
	public Token(TokenType type, ISource source, TextRange textRange)
		: this(type, new(source, textRange))
	{
	}
	
	public Token(TokenType type, SourceLocation sourceLocation)
	{
		Type = type;
		SourceLocation = sourceLocation;
		Line = sourceLocation.Source.GetLineColumn(sourceLocation.Range.Start).Line;
	}
	
	public override string ToString() => $"{Type} [{AsSpan()}]";
}