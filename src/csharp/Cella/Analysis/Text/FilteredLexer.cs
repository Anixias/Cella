using System.Collections;

namespace Cella.Analysis.Text;

public sealed class FilteredLexer : ILexer
{
	public IBuffer Source => lexer.Source;
	
	private readonly Lexer lexer;

	public FilteredLexer(IBuffer source)
	{
		lexer = new(source);
	}

	public ScanResult? ScanToken(int position)
	{
		var result = lexer.ScanToken(position);
		var hadNewline = false;
		while (result is { Token.Type.IsFiltered: true } filteredScanResult)
		{
			if (filteredScanResult.Token.Type == TokenType.Newline)
				hadNewline = true;
			
			result = lexer.ScanToken(filteredScanResult.NextPosition);
		}

		if (hadNewline && result is { } scanResult)
		{
			scanResult.Token.IsAfterNewline = true;
		}
		
		return result;
	}

	private List<Token> ScanAllTokens()
	{
		var tokens = new List<Token>();
		var position = 0;
		
		while (true)
		{
			if (ScanToken(position) is not { } lexerResult)
			{
				return tokens;
			}

			tokens.Add(lexerResult.Token);
			position = lexerResult.NextPosition;
		}
	}

	public IEnumerator<Token> GetEnumerator()
	{
		return ScanAllTokens().GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
}