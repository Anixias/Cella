namespace Cella.Core.Text;

public readonly struct SourceLocation
{
	public static readonly SourceLocation None = new(StringBuffer.Empty, TextRange.Empty);
	
	public readonly IBuffer Source;
	public readonly TextRange Range;
	
	public SourceLocation(IBuffer source, TextRange range)
	{
		Source = source;
		Range = range;
	}
}