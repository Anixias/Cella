namespace Cella.Analysis.Text;

public sealed class TokenType
{
	public bool IsInvalid { get; private init; }
	public bool IsKeyword { get; private init; }
	public bool IsIdentifier { get; private init; }
	public bool IsOperator { get; private init; }
	public bool IsLiteral { get; private init; }
	public bool IsFiltered { get; private init; }
	
	private static readonly Dictionary<string, TokenType> Keywords = new();
	private static readonly Dictionary<string, TokenType> Operators = new();

	private readonly string representation;
	
	private TokenType(string representation)
	{
		this.representation = representation;
	}

	public override string ToString() => representation;

	private static TokenType CreateKeyword(string text)
	{
		var type = new TokenType(text)
		{
			IsKeyword = true
		};

		Keywords.Add(text, type);
		return type;
	}
	
	private static TokenType CreateOperator(string text)
	{
		var type = new TokenType(text)
		{
			IsOperator = true
		};

		Operators.Add(text, type);
		return type;
	}
	
	public static TokenType? GetKeyword(string keyword)
	{
		return Keywords.GetValueOrDefault(keyword);
	}

	public static TokenType? GetOperator(string @operator)
	{
		return Operators.GetValueOrDefault(@operator);
	}
	
	public static readonly TokenType EndOfFile = new("eof")
	{
		IsInvalid = true
	};
	
	public static readonly TokenType Identifier = new("identifier")
	{
		IsIdentifier = true
	};
	
	public static readonly TokenType Invalid = new("invalid")
	{
		IsInvalid = true
	};
	
	public static readonly TokenType Whitespace = new("whitespace")
	{
		IsFiltered = true
	};
	
	public static readonly TokenType LineComment = new("line comment")
	{
		IsFiltered = true
	};

	public static readonly TokenType MultilineComment = new("block comment")
	{
		IsFiltered = true
	};
	
	public static readonly TokenType DocumentationComment = new("documentation comment")
	{
		IsFiltered = true
	};

	public static readonly TokenType NumberLiteral = new("number literal")
	{
		IsLiteral = true
	};

	public static readonly TokenType InvalidNumberLiteral = new("invalid number literal")
	{
		IsLiteral = true,
		IsInvalid = true
	};

	public static readonly TokenType StringLiteral = new("string literal")
	{
		IsLiteral = true
	};

	public static readonly TokenType InterpolatedStringLiteral = new("interpolated string literal")
	{
		IsLiteral = true
	};

	public static readonly TokenType InvalidStringLiteral = new("invalid string literal")
	{
		IsLiteral = true,
		IsInvalid = true
	};

	public static readonly TokenType CharLiteral = new("char literal")
	{
		IsLiteral = true
	};

	public static readonly TokenType InvalidCharLiteral = new("invalid char literal")
	{
		IsLiteral = true,
		IsInvalid = true
	};
	
	#region Keywords
	
	public static readonly TokenType KeywordEntry = CreateKeyword("entry");
	public static readonly TokenType KeywordUse = CreateKeyword("use");
	public static readonly TokenType KeywordAs = CreateKeyword("as");
	public static readonly TokenType KeywordMod = CreateKeyword("mod");
	public static readonly TokenType KeywordType = CreateKeyword("type");
	public static readonly TokenType KeywordUtil = CreateKeyword("util");
	public static readonly TokenType KeywordTrait = CreateKeyword("trait");
	public static readonly TokenType KeywordImpl = CreateKeyword("impl");
	public static readonly TokenType KeywordPub = CreateKeyword("pub");
	public static readonly TokenType KeywordVar = CreateKeyword("var");
	public static readonly TokenType KeywordVal = CreateKeyword("val");
	public static readonly TokenType KeywordMut = CreateKeyword("mut");
	public static readonly TokenType KeywordSelf = CreateKeyword("self");
	public static readonly TokenType KeywordGet = CreateKeyword("get");
	public static readonly TokenType KeywordSet = CreateKeyword("set");
	public static readonly TokenType KeywordThis = CreateKeyword("this");
	public static readonly TokenType KeywordFun = CreateKeyword("fun");
	public static readonly TokenType KeywordRet = CreateKeyword("ret");
	public static readonly TokenType KeywordRef = CreateKeyword("ref");
	public static readonly TokenType KeywordIf = CreateKeyword("if");
	public static readonly TokenType KeywordIs = CreateKeyword("is");
	public static readonly TokenType KeywordIn = CreateKeyword("in");
	public static readonly TokenType KeywordWith = CreateKeyword("with");
	public static readonly TokenType KeywordElse = CreateKeyword("else");
	public static readonly TokenType KeywordEnum = CreateKeyword("enum");
	public static readonly TokenType KeywordFor = CreateKeyword("for");
	public static readonly TokenType KeywordWhile = CreateKeyword("while");
	public static readonly TokenType KeywordCont = CreateKeyword("cont");
	public static readonly TokenType KeywordExit = CreateKeyword("exit");
	public static readonly TokenType KeywordExt = CreateKeyword("ext");
	public static readonly TokenType KeywordDll = CreateKeyword("dll");
	
	#endregion
	#region Operators
	
	public static readonly TokenType OpComma = CreateOperator(",");
	public static readonly TokenType OpDotDotEqual = CreateOperator("..=");
	public static readonly TokenType OpEllipsis = CreateOperator("...");
	public static readonly TokenType OpDotDot = CreateOperator("..");
	public static readonly TokenType OpDot = CreateOperator(".");
	public static readonly TokenType OpColon = CreateOperator(":");
	public static readonly TokenType OpOpenParen = CreateOperator("(");
	public static readonly TokenType OpCloseParen = CreateOperator(")");
	public static readonly TokenType OpOpenBracket = CreateOperator("[");
	public static readonly TokenType OpCloseBracket = CreateOperator("]");
	public static readonly TokenType OpOpenBrace = CreateOperator("{");
	public static readonly TokenType OpCloseBrace = CreateOperator("}");
	public static readonly TokenType OpEqualsEquals = CreateOperator("==");
	public static readonly TokenType OpEquals = CreateOperator("=");
	public static readonly TokenType OpBangEquals = CreateOperator("!=");
	public static readonly TokenType OpBang = CreateOperator("!");
	public static readonly TokenType OpPlusEqual = CreateOperator("+=");
	public static readonly TokenType OpPlusPlus = CreateOperator("++");
	public static readonly TokenType OpPlus = CreateOperator("+");
	public static readonly TokenType OpMinusEqual = CreateOperator("-=");
	public static readonly TokenType OpMinusMinus = CreateOperator("--");
	public static readonly TokenType OpMinus = CreateOperator("-");
	public static readonly TokenType OpTilde = CreateOperator("~");
	public static readonly TokenType OpStarStarEqual = CreateOperator("**=");
	public static readonly TokenType OpStarStar = CreateOperator("**");
	public static readonly TokenType OpStarEqual = CreateOperator("*=");
	public static readonly TokenType OpStar = CreateOperator("*");
	public static readonly TokenType OpSlashEqual = CreateOperator("/=");
	public static readonly TokenType OpSlash = CreateOperator("/");
	public static readonly TokenType OpPercentEqual = CreateOperator("%=");
	public static readonly TokenType OpPercent = CreateOperator("%");
	public static readonly TokenType OpAmpEqual = CreateOperator("&=");
	public static readonly TokenType OpAmp = CreateOperator("&");
	public static readonly TokenType OpBarEqual = CreateOperator("|=");
	public static readonly TokenType OpBar = CreateOperator("|");
	public static readonly TokenType OpHatEqual = CreateOperator("^=");
	public static readonly TokenType OpHat = CreateOperator("^");
	
	#endregion
}