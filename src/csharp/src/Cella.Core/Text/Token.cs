namespace Cella.Core.Text;

public class Token
{
	public IBuffer Source { get; }
	public TokenType Type { get; }
	public TextRange Range { get; }
	public int Line { get; }
	public int Column { get; }
	public SourceLocation SourceLocation { get; }
	public ReadOnlySpan<char> Text => Source.GetText(Range);
	
	/// <summary>
	/// Only utilized by filtered lexer results
	/// </summary>
	public bool IsAfterNewline { get; set; }
	
	public Token(TokenType type, TextRange range, IBuffer source)
		: this(type, range, source, range.Start)
	{
	}
	
	private Token(TokenType type, TextRange range, IBuffer source, int position)
	{
		Source = source;
		Type = type;
		Range = range;
		(Line, Column) = source.GetLineColumn(position);
		SourceLocation = new(Source, Range);
	}
	
	public static Token EndOfFile(IBuffer source) =>
		new(TokenType.EndOfFile, TextRange.EndOfFile, source, source.Length);
	
	public override string ToString() => $"{Type}: {Text}";
}

public sealed class ValueToken<T> : Token
{
	public T Value { get; }
	
	public ValueToken(TokenType type, TextRange range, IBuffer source, T value)
		: base(type, range, source)
	{
		Value = value;
	}
	
	public override string ToString() => Value switch
	{
		null => base.ToString(),
		_ => $"{Type}: {Text} ({Value})"
	};
}