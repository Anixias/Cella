namespace Cella.Core.Text;

public readonly record struct SourceLocation(ISource Source, TextRange Range)
{
	public static readonly SourceLocation None = new(StringSource.Empty, TextRange.Empty);
	public SourceLocation End => new(Source, new(Range.End - 1, 1));
	
	public ReadOnlySpan<char> GetText() => Source.GetText(Range);
	public static implicit operator ReadOnlySpan<char>(SourceLocation sourceLocation) => sourceLocation.GetText();
	
	/// <summary>
	/// Gets the line and column of the start of the range.
	/// </summary>
	/// <returns>The line and column as a tuple.</returns>
	public (int Line, int Column) GetLineColumn() => Source.GetLineColumn(Range.Start);
	
	public override string ToString()
	{
		var start = Source.GetLineColumn(Range.Start);
		var end = Source.GetLineColumn(Range.End - 1);
		return $"{start.Line}:{start.Column} - {end.Line}:{end.Column} ({Source.FilePath})";
	}
}