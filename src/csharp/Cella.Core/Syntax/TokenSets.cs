using Cella.Core.Text;

namespace Cella.Core.Syntax;

internal static class TokenSets
{
	public static readonly HashSet<string> MemberKinds =
		["fun", "op", "prop", "get", "set", "rec", "enum", "union", "trait", "impl", "const", "val", "var"];
	
	public static readonly HashSet<string> MemberModifiers = ["pub", "pvt", "mod", "ref", "own", "ext", "req"];
	
	public static readonly HashSet<TokenType> ParameterModes =
		[TokenType.KeywordMut, TokenType.KeywordImm];
	
	public static readonly HashSet<TokenType> BorrowKeywords = [TokenType.KeywordImm, TokenType.KeywordMut];
	
	public static readonly HashSet<TokenType> StatementKeywords =
	[
		TokenType.KeywordVal, TokenType.KeywordVar, TokenType.KeywordConst, TokenType.KeywordRet, TokenType.KeywordIf,
		TokenType.KeywordFor, TokenType.KeywordWhile, TokenType.KeywordDo, TokenType.KeywordLoop,
		TokenType.KeywordMatch,
		TokenType.KeywordDrop, TokenType.KeywordWhen, TokenType.KeywordBreak, TokenType.KeywordCont,
		TokenType.KeywordPanic
	];
}