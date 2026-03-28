namespace Cella.Core.Text;

public readonly record struct Token(TokenType Type, SourceLocation SourceLocation)
{
	public static Token EndOfFile(ISource source) => new(TokenType.EndOfFile, new(source, TextRange.EndOfFile));
	
	public Token(TokenType type, ISource source, TextRange textRange)
		: this(type, new(source, textRange))
	{
	}
	
	public override string ToString() => $"{Type} [{AsSpan()}]";
	
	public string GetText() => new(AsSpan());
	public ReadOnlySpan<char> AsSpan() => SourceLocation.GetText();
}