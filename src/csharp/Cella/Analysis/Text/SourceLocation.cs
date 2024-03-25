namespace Cella.Analysis.Text;

public readonly struct SourceLocation
{
	public readonly IBuffer source;
	public readonly TextRange range;

	public static readonly SourceLocation None = new();

	public SourceLocation(IBuffer source, TextRange range)
	{
		this.source = source;
		this.range = range;
	}
}