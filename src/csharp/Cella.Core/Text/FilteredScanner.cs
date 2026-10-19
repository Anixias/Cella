using System.Collections;
using System.Collections.Immutable;

namespace Cella.Core.Text;

public class FilteredScanner : IEnumerable<Token>
{
	private readonly Scanner _scanner;
	
	public FilteredScanner(ISource source)
	{
		_scanner = new(source);
	}
	
	public ScanResult ScanToken(int position)
	{
		var pos = position;
		var result = _scanner.ScanToken(pos);
		var hidden = ImmutableArray<SourceLocation>.Empty;
		while (result.NextPosition > pos && result.Token.Type.IsFiltered)
		{
			hidden = hidden.AddRange(result.Token.HiddenCharacters);
			pos = result.NextPosition;
			result = _scanner.ScanToken(pos);
		}
		
		return hidden.IsEmpty
			? result
			: result with
			{
				Token = result.Token with { HiddenCharacters = [..hidden, ..result.Token.HiddenCharacters] }
			};
	}
	
	private IEnumerable<Token> ScanAllTokens()
	{
		var position = 0;
		var codeEnd = 0;
		while (ScanToken(position) is var lexerResult)
		{
			var token = lexerResult.Token;
			if (token.Type == TokenType.EndOfFile)
			{
				yield return Token.EndOfFile(token.SourceLocation.Source, codeEnd) with
				{
					HiddenCharacters = token.HiddenCharacters
				};
				
				break;
			}
			
			yield return token;
			codeEnd = token.SourceLocation.Range.End;
			if (lexerResult.NextPosition <= position)
				break;
			
			position = lexerResult.NextPosition;
		}
	}
	
	public IEnumerator<Token> GetEnumerator() => ScanAllTokens().GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}