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

	private bool MatchSameLine(params TokenType[] types)
	{
		return MatchSameLine(out _, types);
	}

	private bool MatchSameLine([NotNullWhen(true)] out Token? token, params TokenType[] types)
	{
		if (types.Contains(TokenType.EndOfFile))
			throw new ArgumentException("Cannot match EndOfFile token", nameof(types));
		
		token = null;
		
		if (IsEndOfFile())
			return false;
		
		token = tokens[position];

		if (token.IsAfterNewline)
			return false;
		
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

	private bool IsEndOfLine() => Next()?.IsAfterNewline ?? true;

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
			return ParseDeclaration();
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

	private SyntaxNode ParseDeclaration()
	{
		var identifier = Require(null, TokenType.Identifier);
		Require(null, TokenType.OpColon);

		var modifierTypes = TokenType.DeclarationModifiers.ToArray();
		var modifiers = ParseModifiers(modifierTypes);
		
		var peek = Peek();
		if (peek == TokenType.KeywordEntry)
		{
			return ParseEntry(identifier, modifiers);
		}

		if (peek == TokenType.KeywordFun)
		{
			// Todo: return ParseFunction(identifier, modifiers);
		}

		if (peek == TokenType.KeywordType)
		{
			// Todo: return ParseType(identifier, modifiers);
		}

		if (peek == TokenType.KeywordEnum)
		{
			// Todo: return ParseEnum(identifier, modifiers);
		}

		if (peek == TokenType.Identifier)
		{
			// Todo: return ParseVariable(identifier, modifiers);
		}
			
		throw new ParseException("Excepted declaration type", Next()!);
	}

	private List<Token> ParseModifiers(TokenType[] modifierTypes)
	{
		var modifiers = new List<Token>();

		while (true)
		{
			if (!Match(out var match, modifierTypes))
				break;

			if (modifiers.All(m => m.Type != match.Type))
			{
				modifiers.Add(match);
				continue;
			}

			var duplicateError = new ParseException("Duplicate modifier specified", match);
			diagnostics.Add(duplicateError);
		}

		return modifiers;
	}

	private EntryNode ParseEntry(Token identifier, List<Token> modifiers)
	{
		foreach (var modifier in modifiers)
		{
			// All modifiers invalid for entry
			diagnostics.Add(new ParseException($"Modifier '{modifier.Text}' not valid for entry point", modifier));
		}
		
		Require(null, TokenType.KeywordEntry);
		Require(null, TokenType.OpOpenParen);
		var parameters = ParseParameters();
		Require(null, TokenType.OpCloseParen);
		
		var effects = new List<Token>();
		SyntaxType? returnType = null;
		if (Match(TokenType.OpColon))
		{
			if (Peek() == TokenType.OpBang)
			{
				effects.AddRange(ParseEffects());
			}

			returnType = ParseSyntaxType();
		}

		var body = ParseBlock();
		return new EntryNode(identifier, parameters, returnType, effects, body, identifier.Range.Join(body.range));
	}

	private List<Token> ParseEffects()
	{
		var effects = new List<Token>();

		Require(null, TokenType.OpBang);

		if (Match(TokenType.OpOpenBrace))
		{
			do
            {
            	var effect = Require(null, TokenType.Identifier);
    
            	if (effects.Find(e => e.Text == effect.Text) is not null)
            	{
            		diagnostics.Add(new ParseException($"Effect '{effect.Text}' already specified", effect));
            		continue;
            	}
            	
            	effects.Add(effect);
            } while (Match(TokenType.OpComma));
            
            Require(null, TokenType.OpCloseBrace);
		}
		else
		{
			var effect = Require(null, TokenType.Identifier);
			effects.Add(effect);
		}

		return effects;
	}

	private List<SyntaxParameter> ParseParameters()
	{
		var parameters = new List<SyntaxParameter>();
		var allowSelf = true;
		var requireDefaultValue = false;
		Token? variadicParameter = null;
		
		var validSelfModifiers = new[]
		{
			TokenType.KeywordMut
		};
		
		var validNonSelfModifiers = new[]
		{
			TokenType.KeywordVar
		};
		
		var modifierTypes = TokenType.ParameterModifiers.ToArray();
		do
		{
			var alreadyVariadic = variadicParameter is not null;
			var isVariadic = Match(TokenType.OpEllipsis);
			
			var modifiers = ParseModifiers(modifierTypes);
			var identifier = Require(null, TokenType.Identifier, TokenType.KeywordSelf);

			if (isVariadic)
				variadicParameter ??= identifier;

			if (alreadyVariadic)
			{
				diagnostics.Add(new ParseException($"Variadic parameter '{variadicParameter!.Text}' must be the " +
				                                   $"final parameter", identifier));
			}
			
			// `mut` modifier only valid if identifier is `self`
			// Similarly, `var` modifier only valid if identifier is not `self`
			if (identifier.Type == TokenType.KeywordSelf)
			{
				if (isVariadic)
				{
					diagnostics.Add(new ParseException("Parameter 'self' cannot be variadic", identifier));
				}
				
				if (!allowSelf)
				{
					diagnostics.Add(new ParseException("Parameter 'self' only valid as first parameter", identifier));
				}

				foreach (var modifier in modifiers)
				{
					if (modifier.Type == TokenType.KeywordVar)
					{
						diagnostics.Add(new ParseException($"Modifier '{modifier.Text}' not valid for 'self' " +
						                                   $"parameters; Did you mean 'mut'?", identifier));
					}
					else if (!validSelfModifiers.Contains(modifier.Type))
					{
						diagnostics.Add(new ParseException($"Modifier '{modifier.Text}' not valid for 'self' " +
						                                   $"parameters", identifier));
					}
				}
				
				allowSelf = false;
				parameters.Add(new SyntaxParameter.Self(identifier, modifiers));
				continue;
			}
			
			foreach (var modifier in modifiers)
			{
				if (modifier.Type == TokenType.KeywordMut)
				{
					diagnostics.Add(new ParseException($"Modifier '{modifier.Text}' only valid for 'self' " +
					                                   $"parameters; Did you mean 'var'?", identifier));
				}
				else if (!validNonSelfModifiers.Contains(modifier.Type))
				{
					diagnostics.Add(new ParseException($"Modifier '{modifier.Text}' only valid for 'self' " +
					                                   $"parameters", identifier));
				}
			}
			
			Require(null, TokenType.OpColon);
			var type = ParseSyntaxType();
			var defaultValueRange = Next()?.Range ?? TextRange.Empty;

			if (requireDefaultValue)
			{
				if (!TryRequire(
					    $"Parameter '{identifier.Text}' requires default value because previous parameter has " +
					    "default value", TokenType.OpEquals))
				{
					parameters.Add(new SyntaxParameter.Variable(identifier, type, modifiers, isVariadic));
					continue;
				}
			}
			else if (!Match(TokenType.OpEquals))
			{
				parameters.Add(new SyntaxParameter.Variable(identifier, type, modifiers, isVariadic));
				continue;
			}
			
			requireDefaultValue = true;
			var defaultValue = ParseExpression();
			defaultValueRange = defaultValueRange.Join(defaultValue.range);

			if (isVariadic)
			{
				diagnostics.Add(new ParseException("Variadic parameters cannot have default value", source,
					defaultValueRange));
			}
			
			parameters.Add(new SyntaxParameter.Variable(identifier, type, modifiers, isVariadic, defaultValue));
		} while (Match(TokenType.OpComma));

		return parameters;
	}

	private SyntaxType ParseSyntaxType()
	{
		var modifierTypes = TokenType.SyntaxTypeModifiers.ToArray();
		var modifiers = ParseModifiers(modifierTypes);

		var mutIndex = modifiers.FindIndex(m => m.Type == TokenType.KeywordMut);
		var refIndex = modifiers.FindIndex(m => m.Type == TokenType.KeywordRef);

		// 'ref mut' should be 'mut ref'
		if (refIndex >= 0 && mutIndex > refIndex)
		{
			var mutToken = modifiers[mutIndex];
			var refToken = modifiers[refIndex];
			var range = mutToken.Range.Join(refToken.Range);
			diagnostics.Add(new ParseException("Invalid modifiers; Did you mean 'mut ref'?", source, range));
		}
		
		var identifier = Require(null, TokenType.Identifier);
		SyntaxType type = new SyntaxType.Base(identifier);

		while (true)
		{
			if (Match(TokenType.OpOpenBracket))
			{
				var dimensions = 1;
				while (Match(TokenType.OpComma))
				{
					dimensions++;
				}
				
				Require(null, TokenType.OpCloseBracket);
				type = new SyntaxType.Array(type, dimensions);
				continue;
			}
			
			// Todo: Nullable types '?'
			// Todo: Generic types '<...>'

			return type;
		}
	}

	private BlockNode ParseBlock()
	{
		var nodes = new List<SyntaxNode>();
		var startToken = Require(null, TokenType.OpOpenBrace);
		
		// Todo: Parse statements
		
		var endToken = Require(null, TokenType.OpCloseBrace);
		return new BlockNode(nodes, startToken.Range.Join(endToken.Range));
	}

	private SyntaxNode ParseExpression()
	{
		throw new NotImplementedException();
	}
}