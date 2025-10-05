using System.Collections.Immutable;

namespace Cella.Core.Text;

public sealed class TokenType
{
	public bool IsInvalid { get; private init; }
	public bool IsKeyword { get; private init; }
	public bool IsIdentifier { get; private init; }
	public bool IsOperator { get; private init; }
	public bool IsLiteral { get; private init; }
	public bool IsFiltered { get; private init; }
	
	private static readonly Dictionary<string, TokenType> _keywords = [];
	private static readonly Dictionary<string, TokenType> _operators = [];
	
	private readonly string _representation;
	
	private TokenType(string representation)
	{
		_representation = representation;
	}

	public override string ToString() => _representation;

	private static TokenType CreateKeyword(string text)
	{
		var type = new TokenType(text)
		{
			IsKeyword = true
		};
		
		_keywords.Add(text, type);
		return type;
	}
	
	private static TokenType CreateKeywordLiteral(string text)
	{
		var type = new TokenType(text)
		{
			IsKeyword = true,
			IsLiteral = true
		};
		
		_keywords.Add(text, type);
		return type;
	}
	
	private static TokenType CreateOperator(string text)
	{
		var type = new TokenType(text)
		{
			IsOperator = true
		};
		
		_operators.Add(text, type);
		return type;
	}
	
	public static TokenType? GetKeyword(string keyword) => _keywords.GetValueOrDefault(keyword);
	public static TokenType? GetOperator(string @operator) => _operators.GetValueOrDefault(@operator);
	
	public static readonly TokenType EndOfFile = new("end of file")
	{
		IsInvalid = true
	};
	
	public static readonly TokenType Invalid = new("invalid")
	{
		IsInvalid = true
	};
	
	public static readonly TokenType Identifier = new("identifier")
	{
		IsIdentifier = true
	};
	
	public static readonly TokenType Whitespace = new("whitespace")
	{
		IsFiltered = true
	};
	
	public static readonly TokenType Newline = new("newline")
	{
		IsFiltered = true
	};
	
	public static readonly TokenType LineComment = new("line comment")
	{
		IsFiltered = true
	};
	
	public static readonly TokenType IntegerLiteral = new("integer literal")
	{
		IsLiteral = true
	};
	
	#region Keywords
	
	// Literal keywords
	public static readonly TokenType KeywordTrue = CreateKeywordLiteral("true");
	public static readonly TokenType KeywordFalse = CreateKeywordLiteral("false");
	
	// @TODO Contextual keywords (would reuse Identifier token type; list the contextual keyword constants)
	// Global keywords
	public static readonly TokenType KeywordMod = CreateKeyword("mod");
	public static readonly TokenType KeywordEntry = CreateKeyword("entry");
	public static readonly TokenType KeywordRet = CreateKeyword("ret");
	
	#endregion
	#region Operators
	
	public static readonly TokenType OpColon = CreateOperator(":");
	public static readonly TokenType OpSemicolon = CreateOperator(";");
	public static readonly TokenType OpOpenParen = CreateOperator("(");
	public static readonly TokenType OpCloseParen = CreateOperator(")");
	public static readonly TokenType OpOpenBrace = CreateOperator("{");
	public static readonly TokenType OpCloseBrace = CreateOperator("}");
	
	#endregion
}