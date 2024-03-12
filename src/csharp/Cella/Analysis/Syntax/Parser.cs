using System.Diagnostics.CodeAnalysis;
using System.Text;
using Cella.Analysis.Text;
using Cella.Diagnostics;

namespace Cella.Analysis.Syntax;

public sealed class Parser
{
	private readonly IBuffer source;
	private readonly IReadOnlyList<Token> tokens;
	private readonly DiagnosticList diagnostics = new();
	private int position;

	private Parser(IBuffer source, IReadOnlyList<Token> tokens)
	{
		this.source = source;
		this.tokens = tokens;
	}

	public static (Ast? ast, DiagnosticList diagnostics) Parse(ILexer lexer)
	{
		var tokens = new List<Token>();
		foreach (var token in lexer)
		{
			tokens.Add(token);
		}
		
		var parser = new Parser(lexer.Source, tokens);
		return (parser.Parse(), parser.diagnostics);
	}
	
	private bool IsEndOfFile(int position) => position >= tokens.Count;
	private bool IsEndOfFile() => IsEndOfFile(position);

	private Token Require(string? errorMessage, params TokenType[] types)
	{
		if (errorMessage is null)
		{
			var typeString = new StringBuilder();

			if (types.Length == 1)
				typeString.Append($"'{types[0]}'");
			else
			{
				typeString.Append("one of: ");
				typeString.AppendJoin(',', types.Select(t => $"'{t}'"));
			}

			errorMessage = $"Expected {typeString}";
		}

		if (IsEndOfFile())
		{
			throw tokens.Count > 0
				? new ParseException($"{errorMessage}; Instead, got 'end of file'", tokens.Last())
				: new ParseException($"{errorMessage}; Instead, got 'end of file'", source, TextRange.Empty);
		}

		var token = tokens[position];
		if (!types.Contains(token.Type))
			throw new ParseException($"{errorMessage}; Instead, got '{token.Type}'", token);
		
		position++;
		return token;
	}

	private bool TryMatch(params TokenType[] types)
	{
		return TryMatch(out _, types);
	}

	private bool TryMatch([NotNullWhen(true)] out Token? token, params TokenType[] types)
	{
		if (types.Contains(TokenType.EndOfFile))
			throw new ArgumentException("Cannot match EndOfFile token", nameof(types));
		
		token = null;
		
		if (IsEndOfFile())
			return false;
		
		token = tokens[position];
		
		if (!types.Contains(token.Type))
			return false;
		
		position++;
		return true;
	}

	private TokenType Peek()
	{
		return TokenAt(position)?.Type ?? TokenType.EndOfFile;
	}

	private Token? TokenAt(int position)
	{
		return IsEndOfFile(position) ? null : tokens[position];
	}

	private Token? Next() => TokenAt(position);

	private Ast? Parse()
	{
		try
		{
			var root = ParseProgram();
			return root is null ? null : new Ast(root, source);
		}
		catch (ParseException e)
		{
			diagnostics.Add(e);
			return null;
		}
	}

	private ProgramNode? ParseProgram()
	{
		Require("All Cella files must begin with a module name: 'mod <name>'", TokenType.KeywordMod);
		var modName = ParseModuleName();
		var statements = ParseTopLevelStatements();

		if (TokenAt(position) is { } token)
		{
			diagnostics.Add(new ParseException("Expected end of file", token));
		}

		if (diagnostics.ErrorCount > 0)
			return null;

		return new ProgramNode(modName, [], statements, new TextRange(0, source.Length - 1));
	}

	private ModuleName ParseModuleName()
	{
		var nameTokens = new List<Token>();
		do
		{
			var identifier = Require(null, TokenType.Identifier);
			nameTokens.Add(identifier);
		} while (TryMatch(TokenType.OpDot));

		return new ModuleName(nameTokens);
	}

	private IEnumerable<SyntaxNode> ParseTopLevelStatements()
	{
		var syncTokens = new[]
		{
			TokenType.EndOfFile,
			TokenType.KeywordMod,
			TokenType.KeywordUse,
			TokenType.Identifier
		};
		
		var statements = new List<SyntaxNode>();

		while (Peek() != TokenType.EndOfFile)
		{
			try
			{
				statements.Add(ParseTopLevelStatement());
			}
			catch (ParseException e)
			{
				diagnostics.Add(e);

				do
				{
					position++;
				} while (!syncTokens.Contains(Peek()));
			}
		}
		
		return statements;
	}

	private SyntaxNode ParseTopLevelStatement()
	{
		if (Next() is not { } nextToken)
		{
			throw new ParseException("Expected top-level statement; Instead, got end of file", source, 
				tokens.LastOrDefault()?.Range ?? TextRange.Empty);
		}
		
		if (nextToken.Type == TokenType.KeywordUse)
		{
			// Todo: return ParseImport();
		}
		
		if (nextToken.Type == TokenType.Identifier)
		{
			// Todo: return ParseDeclaration();
		}

		throw new ParseException($"Expected top-level statement; Instead, got '{nextToken.Type}'", nextToken);
	}
}