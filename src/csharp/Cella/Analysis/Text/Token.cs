namespace Cella.Analysis.Text;

public sealed class Token
{
	public IBuffer Source { get; }
	public TokenType Type { get; }
	public TextRange Range { get; }
	public object? Value { get; }
	public int Line { get; }
	public int Column { get; }
	public string Text => Source.GetText(Range);
	public SourceLocation SourceLocation => new(Source, Range);
	
	/// <summary>
	/// Only utilized by filtered lexer results
	/// </summary>
	public bool IsAfterNewline { get; set; }
	
	public Token(TokenType type, TextRange range, IBuffer source, object? value = null)
	{
		Source = source;
		Type = type;
		Range = range;
		Value = value;
		(Line, Column) = source.GetLineColumn(range.Start);
	}

	public override string ToString()
	{
		if (Value is null)
			return $"{Type}: {Text}";
		
		return $"{Type}: {Text} ({Value})";
	}
}