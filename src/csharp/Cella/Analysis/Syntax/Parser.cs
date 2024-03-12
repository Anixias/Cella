using System.Collections.Immutable;
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

	private bool TryRequire(string? errorMessage, params TokenType[] types)
	{
		return TryRequire(out _, errorMessage, types);
	}
	
	private bool TryRequire([NotNullWhen(true)] out Token? token, string? errorMessage, params TokenType[] types)
	{
		token = null;
		var startPosition = position;
		try
		{
			token = Require(errorMessage, types);
			return true;
		}
		catch (ParseException e)
		{
			diagnostics.Add(e);
			position = startPosition;
			return false;
		}
	}

	private bool Match(params TokenType[] types)
	{
		return Match(out _, types);
	}

	private bool Match([NotNullWhen(true)] out Token? token, params TokenType[] types)
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
		const string errorRequireModuleName = "All Cella files must begin with a module name: 'mod <name>'";
		var hasModName = TryRequire(errorRequireModuleName, TokenType.KeywordMod);
		var modName = hasModName ? ParseModuleName() : ModuleName.Error;
		var statements = ParseTopLevelStatements();

		if (TokenAt(position) is { } token)
		{
			diagnostics.Add(new ParseException("Expected end of file", token));
		}

		if (!hasModName || diagnostics.ErrorCount > 0)
			return null;

		return new ProgramNode(modName, statements, new TextRange(0, source.Length - 1));
	}

	private ModuleName ParseModuleName()
	{
		var nameTokens = new List<Token>();
		do
		{
			var identifier = Require(null, TokenType.Identifier);
			nameTokens.Add(identifier);
		} while (Match(TokenType.OpDot));

		return new ModuleName(nameTokens);
	}

	private List<SyntaxNode> ParseTopLevelStatements()
	{
		var syncTokens = new[]
		{
			TokenType.EndOfFile,
			TokenType.KeywordMod,
			TokenType.KeywordUse,
			TokenType.Identifier
		};
		
		var statements = new List<SyntaxNode>();
		var allowImports = true;

		while (Peek() != TokenType.EndOfFile)
		{
			try
			{
				var statement = ParseTopLevelStatement();
				statements.Add(statement);

				var statementIsImport = statement is ImportNode or AggregateImportNode;
				if (!statementIsImport)
				{
					allowImports = false;
					continue;
				}

				if (allowImports)
					continue;
				
				const string errorImportsMustBeFirst =
					"Top-level import statements must appear before any other statements";

				diagnostics.Add(new ParseException(errorImportsMustBeFirst, source, statement.range));
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
			return ParseImport();
		}
		
		if (nextToken.Type == TokenType.Identifier)
		{
			// Todo: return ParseDeclaration();
		}

		throw new ParseException($"Expected top-level statement; Instead, got '{nextToken.Type}'", nextToken);
	}

	private SyntaxNode ParseImport()
	{
		var startToken = Require(null, TokenType.KeywordUse);
		var identifierTokens = new List<Token>();
		var importTokens = new List<ImportToken>();
		var isAggregate = false;
		var range = startToken.Range;
		
		do
		{
			if (Match(out var identifier, TokenType.Identifier, TokenType.OpStar))
			{
				identifierTokens.Add(identifier);
				range = range.Join(identifier.Range);
			}
			else if (Match(TokenType.OpOpenBrace))
			{
				do
				{
					var identifierToken = Require(null, TokenType.Identifier);
					Token? tokenAlias = null;
					if (Match(TokenType.KeywordAs))
					{
						tokenAlias = Require(null, TokenType.Identifier);
					}
					
					importTokens.Add(new ImportToken(identifierToken, tokenAlias));
				} while (Match(TokenType.OpComma));
				
				var endToken = Require(null, TokenType.OpCloseBrace);
				range = range.Join(endToken.Range);
				isAggregate = true;
				break;
			}
		} while (Match(TokenType.OpDot));

		if (identifierTokens.Count < (isAggregate ? 1 : 2))
		{
			if (identifierTokens.LastOrDefault() is { } token)
				throw new ParseException("Invalid import statement", token);
			
			throw new ParseException("Invalid import statement", source, TextRange.Empty);
		}

		var scope = identifierTokens.Take(identifierTokens.Count - 1).ToImmutableArray();
		foreach (var token in scope)
		{
			if (token.Type != TokenType.Identifier)
				throw new ParseException("Invalid import statement: Expected 'identifier'", token);
		}

		Token? alias = null;
		if (Match(TokenType.KeywordAs))
		{
			alias = Require(null, TokenType.Identifier);
			range = range.Join(alias.Range);
		}

		var moduleName = new ModuleName(scope);
		if (!isAggregate)
		{
			return new ImportNode(moduleName, identifierTokens.Last(), alias, range);
		}
		
		return new AggregateImportNode(moduleName, importTokens, alias, range);
	}
}