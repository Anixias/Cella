namespace Cella.Analysis.Text;

public readonly struct ScanResult
{
	public Token Token { get; }
	public int NextPosition { get; }
	
	public ScanResult(Token token, int nextPosition)
	{
		Token = token;
		NextPosition = nextPosition;
	}
}