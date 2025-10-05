namespace Cella.Core.Text;

public readonly record struct ScanResult(Token Token, int NextPosition)
{
	public static ScanResult EndOfFile(ISource source) => new(Token.EndOfFile(source), -1);
}