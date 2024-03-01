namespace Cella.Analysis.Text;

public interface ILexer : IEnumerable<Token>
{
	IBuffer Source { get; }
	ScanResult? ScanToken(int position);
}