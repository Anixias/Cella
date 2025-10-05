namespace Cella.Core.Text;

public interface IScanner : IEnumerable<Token>
{
	ISource Source { get; }
	ScanResult ScanToken(int position);
}