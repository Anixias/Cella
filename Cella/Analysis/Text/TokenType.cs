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
	public static readonly TokenType KeywordLet = CreateKeyword("let");
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
	
	#endregion
}