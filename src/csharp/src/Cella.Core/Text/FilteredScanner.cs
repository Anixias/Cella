using System.Collections;

namespace Cella.Core.Text;

public class FilteredScanner : IScanner
{
	public ISource Source => _scanner.Source;
	private readonly Scanner _scanner;
	
	public FilteredScanner(ISource source)
	{
		_scanner = new(source);
	}
	
	public ScanResult ScanToken(int position)
	{
		var pos = position;
		var result = _scanner.ScanToken(pos);
		while (result.NextPosition > pos && result.Token.Type.IsFiltered)
		{
			pos = result.NextPosition;
			result = _scanner.ScanToken(pos);
		}
		
		return result;
	}
	
	private IEnumerable<Token> ScanAllTokens()
	{
		var position = 0;
		while (ScanToken(position) is var lexerResult)
		{
			yield return lexerResult.Token;
			
			if (lexerResult.NextPosition <= position)
				break;
			
			position = lexerResult.NextPosition;
		}
	}
	
	public IEnumerator<Token> GetEnumerator() => ScanAllTokens().GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}