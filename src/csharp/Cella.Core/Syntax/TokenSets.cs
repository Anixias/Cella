using Cella.Core.Text;

namespace Cella.Core.Syntax;

internal static class TokenSets
{
	public static readonly HashSet<TokenType> ParameterModes =
		[TokenType.KeywordMut, TokenType.KeywordOwn, TokenType.KeywordImm];
	
	public static readonly HashSet<TokenType> BorrowKeywords = [TokenType.KeywordImm, TokenType.KeywordMut];
}