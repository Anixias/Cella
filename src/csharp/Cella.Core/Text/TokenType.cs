using System.Collections.Immutable;

namespace Cella.Core.Text;

public enum TokenType
{
	// Special
	Invalid = -1,
	EndOfFile,
	Identifier,
	
	// Filtered
	Whitespace,
	Newline,
	LineComment,
	BlockComment,
	
	// Literals
	IntegerLiteral,
	StringLiteral,
	CharLiteral,
	InvalidCharLiteral,
	
	// Literal keywords
	KeywordTrue,
	KeywordFalse,
	KeywordNull,
	KeywordUndef,
	KeywordSizeOf,
	
	// Global keywords
	KeywordRet,
	KeywordVar,
	KeywordIf,
	KeywordElse,
	KeywordFor,
	KeywordIn,
	KeywordLoop,
	KeywordWhile,
	KeywordBreak,
	KeywordCont,
	KeywordHeap,
	
	// Contextual Keywords
	KeywordMod,
	KeywordFun,
	KeywordUse,
	KeywordPub,
	KeywordExt,
	KeywordRec,
	
	// Operators
	OpDotDotEqual,
	OpDotDot,
	OpArrow,
	OpPlusEqual,
	OpMinusEqual,
	OpStarEqual,
	OpSlashEqual,
	OpPercentEqual,
	OpEqualEqual,
	OpBangEqual,
	OpGreaterEqual,
	OpLessEqual,
	OpAmpersandEqual,
	OpBarEqual,
	OpHatEqual,
	OpAmpersandAmpersand,
	OpBarBar,
	OpColon,
	OpSemicolon,
	OpOpenParen,
	OpCloseParen,
	OpOpenBracket,
	OpCloseBracket,
	OpOpenBrace,
	OpCloseBrace,
	OpPlus,
	OpMinus,
	OpStar,
	OpSlash,
	OpPercent,
	OpDot,
	OpComma,
	OpEqual,
	OpBang,
	OpTilde,
	OpAmpersand,
	OpBar,
	OpHat,
	OpGreater,
	OpLess,
	OpAt,
}

public static class TokenTypeInfo
{
	private sealed record TokenMetadata(
		string Representation,
		bool IsInvalid = false,
		bool IsKeyword = false,
		bool IsContextual = false,
		bool IsOperator = false,
		bool IsLiteral = false,
		bool IsFiltered = false
	);
	
	private static readonly ImmutableDictionary<TokenType, TokenMetadata> _metadata;
	private static readonly ImmutableDictionary<string, TokenType> _keywords;
	private static readonly ImmutableDictionary<string, TokenType> _operators;
	
	static TokenTypeInfo()
	{
		_metadata = new Dictionary<TokenType, TokenMetadata>
		{
			[TokenType.EndOfFile] = new("end of file", IsInvalid: true),
			[TokenType.Invalid] = new("invalid", IsInvalid: true),
			[TokenType.Identifier] = new("identifier"),
			
			[TokenType.Whitespace] = new("whitespace", IsFiltered: true),
			[TokenType.Newline] = new("newline", IsFiltered: true),
			[TokenType.LineComment] = new("line comment", IsFiltered: true),
			[TokenType.BlockComment] = new("block comment", IsFiltered: true),
			
			[TokenType.IntegerLiteral] = new("integer literal", IsLiteral: true),
			[TokenType.StringLiteral] = new("string literal", IsLiteral: true),
			[TokenType.CharLiteral] = new("char literal", IsLiteral: true),
			[TokenType.InvalidCharLiteral] = new("invalid char literal", IsLiteral: true, IsInvalid: true),
			
			[TokenType.KeywordTrue] = new("true", IsKeyword: true, IsLiteral: true),
			[TokenType.KeywordFalse] = new("false", IsKeyword: true, IsLiteral: true),
			[TokenType.KeywordNull] = new("null", IsKeyword: true, IsLiteral: true),
			[TokenType.KeywordUndef] = new("undef", IsKeyword: true, IsLiteral: true),
			[TokenType.KeywordSizeOf] = new("sizeOf", IsKeyword: true, IsLiteral: true),
			
			[TokenType.KeywordRet] = new("ret", IsKeyword: true),
			[TokenType.KeywordVar] = new("var", IsKeyword: true),
			[TokenType.KeywordIf] = new("if", IsKeyword: true),
			[TokenType.KeywordElse] = new("else", IsKeyword: true),
			[TokenType.KeywordFor] = new("for", IsKeyword: true),
			[TokenType.KeywordIn] = new("in", IsKeyword: true),
			[TokenType.KeywordLoop] = new("loop", IsKeyword: true),
			[TokenType.KeywordWhile] = new("while", IsKeyword: true),
			[TokenType.KeywordBreak] = new("break", IsKeyword: true),
			[TokenType.KeywordCont] = new("cont", IsKeyword: true),
			[TokenType.KeywordHeap] = new("heap", IsKeyword: true),
			
			[TokenType.KeywordMod] = new("mod", IsKeyword: true, IsContextual: true),
			[TokenType.KeywordFun] = new("fun", IsKeyword: true, IsContextual: true),
			[TokenType.KeywordUse] = new("use", IsKeyword: true, IsContextual: true),
			[TokenType.KeywordPub] = new("pub", IsKeyword: true, IsContextual: true),
			[TokenType.KeywordExt] = new("ext", IsKeyword: true, IsContextual: true),
			[TokenType.KeywordRec] = new("rec", IsKeyword: true, IsContextual: true),
			
			[TokenType.OpDotDotEqual] = new("..=", IsOperator: true),
			[TokenType.OpDotDot] = new("..", IsOperator: true),
			[TokenType.OpArrow] = new("->", IsOperator: true),
			[TokenType.OpPlusEqual] = new("+=", IsOperator: true),
			[TokenType.OpMinusEqual] = new("-=", IsOperator: true),
			[TokenType.OpStarEqual] = new("*=", IsOperator: true),
			[TokenType.OpSlashEqual] = new("/=", IsOperator: true),
			[TokenType.OpPercentEqual] = new("%=", IsOperator: true),
			[TokenType.OpEqualEqual] = new("==", IsOperator: true),
			[TokenType.OpBangEqual] = new("!=", IsOperator: true),
			[TokenType.OpGreaterEqual] = new(">=", IsOperator: true),
			[TokenType.OpLessEqual] = new("<=", IsOperator: true),
			[TokenType.OpAmpersandEqual] = new("&=", IsOperator: true),
			[TokenType.OpBarEqual] = new("|=", IsOperator: true),
			[TokenType.OpHatEqual] = new("^=", IsOperator: true),
			[TokenType.OpAmpersandAmpersand] = new("&&", IsOperator: true),
			[TokenType.OpBarBar] = new("||", IsOperator: true),
			[TokenType.OpColon] = new(":", IsOperator: true),
			[TokenType.OpSemicolon] = new(";", IsOperator: true),
			[TokenType.OpOpenParen] = new("(", IsOperator: true),
			[TokenType.OpCloseParen] = new(")", IsOperator: true),
			[TokenType.OpOpenBracket] = new("[", IsOperator: true),
			[TokenType.OpCloseBracket] = new("]", IsOperator: true),
			[TokenType.OpOpenBrace] = new("{", IsOperator: true),
			[TokenType.OpCloseBrace] = new("}", IsOperator: true),
			[TokenType.OpPlus] = new("+", IsOperator: true),
			[TokenType.OpMinus] = new("-", IsOperator: true),
			[TokenType.OpStar] = new("*", IsOperator: true),
			[TokenType.OpSlash] = new("/", IsOperator: true),
			[TokenType.OpPercent] = new("%", IsOperator: true),
			[TokenType.OpDot] = new(".", IsOperator: true),
			[TokenType.OpComma] = new(",", IsOperator: true),
			[TokenType.OpEqual] = new("=", IsOperator: true),
			[TokenType.OpBang] = new("!", IsOperator: true),
			[TokenType.OpTilde] = new("~", IsOperator: true),
			[TokenType.OpAmpersand] = new("&", IsOperator: true),
			[TokenType.OpBar] = new("|", IsOperator: true),
			[TokenType.OpHat] = new("^", IsOperator: true),
			[TokenType.OpGreater] = new(">", IsOperator: true),
			[TokenType.OpLess] = new("<", IsOperator: true),
			[TokenType.OpAt] = new("@", IsOperator: true),
		}.ToImmutableDictionary();
		
		_keywords = _metadata.Where(static kvp => kvp.Value is { IsKeyword: true, IsContextual: false })
			.ToImmutableDictionary(KeySelector, ElementSelector);
		
		_operators = _metadata.Where(static kvp => kvp.Value.IsOperator)
			.ToImmutableDictionary(KeySelector, ElementSelector);
		
		return;
		
		static string KeySelector(KeyValuePair<TokenType, TokenMetadata> kvp) => kvp.Value.Representation;
		static TokenType ElementSelector(KeyValuePair<TokenType, TokenMetadata> kvp) => kvp.Key;
	}
	
	extension(TokenType t)
	{
		public string Representation => _metadata[t].Representation;
		public bool IsInvalid => _metadata[t].IsInvalid;
		public bool IsKeyword => _metadata[t].IsKeyword;
		public bool IsContextual => _metadata[t].IsContextual;
		public bool IsOperator => _metadata[t].IsOperator;
		public bool IsLiteral => _metadata[t].IsLiteral;
		public bool IsFiltered => _metadata[t].IsFiltered;
		
		public static TokenType? GetKeyword(string text) => _keywords.TryGetValue(text, out var type) ? type : null;
		public static TokenType? GetOperator(string text) => _operators.TryGetValue(text, out var type) ? type : null;
	}
}