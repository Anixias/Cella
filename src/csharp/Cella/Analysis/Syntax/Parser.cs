using System.Collections.Immutable;
using System.Diagnostics;
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
		catch (Exception e)
		{
			var range = Next()?.Range;
			var stackTrace = new StackTrace(e, true);
			var stackFrame = stackTrace.GetFrame(0);
			var method = e.TargetSite is null ? "Unknown method" : $"{e.TargetSite.Name}()";
			var location = stackFrame is null ? "Unknown" : $"{method}, line {stackFrame.GetFileLineNumber()}";
			var message = $"Parser failed with {e.GetType().Name} at {location}:\n\t{e.Message}";
			diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, source, range, message));
			return null;
		}
	}

	private ProgramStatement? ParseProgram()
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

		return new ProgramStatement(source, modName, statements, new TextRange(0, source.Length - 1));
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

	private List<StatementNode> ParseTopLevelStatements()
	{
		var syncTokens = new[]
		{
			TokenType.EndOfFile,
			TokenType.KeywordMod,
			TokenType.KeywordUse,
			TokenType.Identifier
		};
		
		var statements = new List<StatementNode>();
		var allowImports = true;

		while (Peek() != TokenType.EndOfFile)
		{
			try
			{
				var statement = ParseTopLevelStatement();
				statements.Add(statement);

				var statementIsImport = statement is ImportStatement or AggregateImportStatement;
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

	private StatementNode ParseTopLevelStatement()
	{
		if (Next() is not { } nextToken)
		{
			throw new ParseException("Expected top-level statement; Instead, got end of file", source, 
				tokens.LastOrDefault()?.Range ?? TextRange.Empty);
		}
		
		if (nextToken.Type == TokenType.KeywordUse)
		{
			return ParseImportStatement();
		}
		
		if (nextToken.Type == TokenType.Identifier)
		{
			return ParseDeclarationStatement();
		}

		throw new ParseException($"Expected top-level statement; Instead, got '{nextToken.Type}'", nextToken);
	}

	private StatementNode ParseImportStatement()
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
			return new ImportStatement(moduleName, identifierTokens.Last(), alias, range);
		}
		
		return new AggregateImportStatement(moduleName, importTokens, alias, range);
	}

	private StatementNode ParseDeclarationStatement()
	{
		var identifier = Require(null, TokenType.Identifier);
		Require(null, TokenType.OpColon);

		var modifierTypes = TokenType.DeclarationModifiers.ToArray();
		var modifiers = ParseModifiers(modifierTypes);
		
		var peek = Peek();
		if (peek == TokenType.KeywordEntry)
		{
			return ParseEntryStatement(identifier, modifiers);
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

	private EntryStatement ParseEntryStatement(Token identifier, List<Token> modifiers)
	{
		foreach (var modifier in modifiers)
		{
			// All modifiers invalid for entry
			diagnostics.Add(new ParseException($"Modifier '{modifier.Text}' not valid for entry point", modifier));
		}
		
		Require(null, TokenType.KeywordEntry);
		Require(null, TokenType.OpOpenParen);
		var parameters = ParseParameters(TokenType.OpCloseParen);
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

		var body = ParseBlockStatement();
		return new EntryStatement(identifier, parameters, returnType, effects, body, identifier.Range.Join(body.range));
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

	private List<SyntaxParameter> ParseParameters(params TokenType[] closeTokens)
	{
		var parameters = new List<SyntaxParameter>();

		if (closeTokens.Contains(Peek()))
			return parameters;
		
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
		if (Peek() == TokenType.OpOpenParen)
			return ParseTupleType();
		
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
				
				var endToken = Require(null, TokenType.OpCloseBracket);
				type = new SyntaxType.Array(type, dimensions, type.range.Join(endToken.Range));
				continue;
			}
			
			// Todo: Nullable types '?'
			// Todo: Generic types '<...>'

			return type;
		}
	}

	private BlockStatement ParseBlockStatement()
	{
		var startToken = Require(null, TokenType.OpOpenBrace);
		var nodes = ParseStatements(TokenType.OpCloseBrace);
		var endToken = Require(null, TokenType.OpCloseBrace);
		return new BlockStatement(nodes, startToken.Range.Join(endToken.Range));
	}
	
	private List<StatementNode> ParseStatements(params TokenType[] endTokens)
	{
		var syncTokens = new[]
		{
			TokenType.EndOfFile,
			TokenType.KeywordMod,
			TokenType.KeywordUse,
			TokenType.KeywordIf,
			TokenType.KeywordFor,
			TokenType.KeywordWhile,
			TokenType.KeywordVal,
			TokenType.KeywordVar
		};
		
		var statements = new List<StatementNode>();

		var peek = Peek();
		while (peek != TokenType.EndOfFile && !endTokens.Contains(peek))
		{
			try
			{
				var statement = ParseStatement();
				statements.Add(statement);
			}
			catch (ParseException e)
			{
				diagnostics.Add(e);

				do
				{
					position++;
				} while (!syncTokens.Contains(Peek()));
			}

			peek = Peek();
		}
		
		return statements;
	}

	private StatementNode ParseStatement()
	{
		if (Next() is not { } nextToken)
		{
			throw new ParseException("Expected statement; Instead, got end of file", source, 
				tokens.LastOrDefault()?.Range ?? TextRange.Empty);
		}
		
		if (nextToken.Type == TokenType.KeywordUse)
		{
			return ParseImportStatement();
		}
		
		if (nextToken.Type == TokenType.KeywordRet)
		{
			return ParseReturnStatement();
		}
		
		if (nextToken.Type == TokenType.Identifier)
		{
			return ParseDeclarationStatement();
		}
		
		throw new ParseException($"Expected statement; Instead, got '{nextToken.Type}'", nextToken);
	}

	private ReturnStatement ParseReturnStatement()
	{
		var startToken = Require(null, TokenType.KeywordRet);
		
		if (Match(out var voidToken, TokenType.KeywordVoid))
		{
			return new ReturnStatement(null, startToken.Range.Join(voidToken.Range));
		}
		
		var expression = ParseExpression();
		return new ReturnStatement(expression, startToken.Range.Join(expression.range));
	}

	private ExpressionNode ParseExpression()
	{
		if (TryParseLambdaExpression(out var lambdaExpression))
			return lambdaExpression;
		
		return ParseAssignmentExpression();
	}

	private bool TryParseLambdaExpression([NotNullWhen(true)] out LambdaExpression? lambdaExpression)
	{
		var startPosition = position;

		try
		{
			lambdaExpression = ParseLambdaExpression();
			return true;
		}
		catch
		{
			lambdaExpression = null;
			position = startPosition;
			return false;
		}
	}

	private LambdaExpression ParseLambdaExpression()
	{
		// Todo: Implement Lambda expressions
		throw new NotImplementedException();
	}

	private ExpressionNode ParseAssignmentExpression()
	{
		var expression = ParseConditionalExpression();

		if (!Match(out var op, TokenType.OpEquals, TokenType.OpPlusEqual, TokenType.OpMinusEqual, 
			    TokenType.OpStarStarEqual, TokenType.OpStarEqual, TokenType.OpSlashEqual, TokenType.OpAmpEqual, 
			    TokenType.OpBarEqual, TokenType.OpHatEqual, TokenType.OpPercentEqual))
			return expression;
		
		var valueExpression = ParseExpression();
		return new AssignmentExpression(expression, op, valueExpression, expression.range.Join(valueExpression.range));
	}
	
	private ExpressionNode ParseConditionalExpression()
	{
		var expression = ParseNullCoalescingExpression();

		if (!Match(TokenType.OpQuestion))
			return expression;
		
		var trueExpression = ParseExpression();
		var range = expression.range.Join(trueExpression.range);

		ExpressionNode? falseExpression = null;
		if (Match(TokenType.OpColon))
		{
			falseExpression = ParseExpression();
			range = range.Join(falseExpression.range);
		}

		return new ConditionalExpression(expression, trueExpression, falseExpression, range);
	}
	
	private ExpressionNode ParseNullCoalescingExpression()
	{
		var expression = ParseEqualityExpression();

		while (Match(out var op, TokenType.OpQuestionQuestion))
		{
			var right = ParseExpression();
			var range = expression.range.Join(right.range);
			expression = new BinaryExpression(expression, BinaryExpression.Operation.NullCoalescence, op, right, range);
		}

		return expression;
	}
	
	private ExpressionNode ParseEqualityExpression()
	{
		var expression = ParseOrExpression();
		
		while (Match(out var op, TokenType.OpEqualsEquals, TokenType.OpBangEquals))
		{
			var right = ParseOrExpression();
			var range = expression.range.Join(right.range);

			var operation = op.Type == TokenType.OpEqualsEquals
				? BinaryExpression.Operation.Equals
				: BinaryExpression.Operation.NotEquals;
			
			expression = new BinaryExpression(expression, operation, op, right, range);
		}

		return expression;
	}

	private ExpressionNode ParseOrExpression()
	{
		var expression = ParseXorExpression();
		
		while (Match(out var op, TokenType.OpBar))
		{
			var right = ParseXorExpression();
			var range = expression.range.Join(right.range);
			expression = new BinaryExpression(expression, BinaryExpression.Operation.Or, op, right, range);
		}

		return expression;
	}

	private ExpressionNode ParseXorExpression()
	{
		var expression = ParseAndExpression();
		
		while (Match(out var op, TokenType.OpHat))
		{
			var right = ParseAndExpression();
			var range = expression.range.Join(right.range);
			expression = new BinaryExpression(expression, BinaryExpression.Operation.Xor, op, right, range);
		}

		return expression;
	}

	private ExpressionNode ParseAndExpression()
	{
		var expression = ParseRelationalExpression();
		
		while (Match(out var op, TokenType.OpAmp))
		{
			var right = ParseRelationalExpression();
			var range = expression.range.Join(right.range);
			expression = new BinaryExpression(expression, BinaryExpression.Operation.And, op, right, range);
		}

		return expression;
	}

	private ExpressionNode ParseRelationalExpression()
	{
		var expression = ParseShiftExpression();
		var range = expression.range;

		if (Match(out var op, TokenType.OpLessEqual, TokenType.OpGreaterEqual, TokenType.OpLess, TokenType.OpGreater)) 
		{
			var right = ParseShiftExpression();
			range = range.Join(right.range);
			
			BinaryExpression.Operation operation;
			if (op.Type == TokenType.OpLessEqual)
				operation = BinaryExpression.Operation.LessEqual;
			else if (op.Type == TokenType.OpGreaterEqual)
				operation = BinaryExpression.Operation.GreaterEqual;
			else if (op.Type == TokenType.OpLess)
				operation = BinaryExpression.Operation.LessThan;
			else if (op.Type == TokenType.OpGreater)
				operation = BinaryExpression.Operation.GreaterThan;
			else
				throw new InvalidOperationException($"Unexpected relational operation '{op.Text}'");
			
			return new BinaryExpression(expression, operation, op, right, range);
		}

		if (!Match(out var castOp, TokenType.KeywordIs, TokenType.KeywordAs))
			return expression;
		
		var castType = ParseSyntaxType();
		range = expression.range.Join(castType.range);

		var castOperation = castOp.Type == TokenType.KeywordIs
			? CastExpression.Operation.Is
			: CastExpression.Operation.As;
		
		return new CastExpression(expression, castOperation, castOp, castType, range);
	}
	
	private ExpressionNode ParseShiftExpression()
	{
		var expression = ParseAdditiveExpression();

		while (Match(out var op, TokenType.OpRotLeft, TokenType.OpRotRight, TokenType.OpLeftLeft,
			TokenType.OpRightRight))
		{
			var right = ParseAdditiveExpression();
			var range = expression.range.Join(right.range);
			
			BinaryExpression.Operation operation;
			if (op.Type == TokenType.OpRotLeft)
				operation = BinaryExpression.Operation.RotLeft;
			else if (op.Type == TokenType.OpRotRight)
				operation = BinaryExpression.Operation.RotRight;
			else if (op.Type == TokenType.OpLeftLeft)
				operation = BinaryExpression.Operation.ShiftLeft;
			else if (op.Type == TokenType.OpRightRight)
				operation = BinaryExpression.Operation.ShiftRight;
			else
				throw new InvalidOperationException($"Unexpected shift operation '{op.Text}'");
			
			expression = new BinaryExpression(expression, operation, op, right, range);
		}

		return expression;
	}
	
	private ExpressionNode ParseAdditiveExpression()
	{
		var expression = ParseMultiplicativeExpression();

		while (Match(out var op, TokenType.OpPlus, TokenType.OpMinus))
		{
			var right = ParseMultiplicativeExpression();
			var range = expression.range.Join(right.range);
			
			BinaryExpression.Operation operation;
			if (op.Type == TokenType.OpPlus)
				operation = BinaryExpression.Operation.Add;
			else if (op.Type == TokenType.OpMinus)
				operation = BinaryExpression.Operation.Subtract;
			else
				throw new InvalidOperationException($"Unexpected additive operation '{op.Text}'");
			
			expression = new BinaryExpression(expression, operation, op, right, range);
		}

		return expression;
	}

	private ExpressionNode ParseMultiplicativeExpression()
	{
		var expression = ParseExponentiationExpression();

		while (Match(out var op, TokenType.OpStar, TokenType.OpSlash, TokenType.OpPercentPercent, TokenType.OpPercent)) 
		{
			var right = ParseExponentiationExpression();
			var range = expression.range.Join(right.range);
			
			BinaryExpression.Operation operation;
			if (op.Type == TokenType.OpStar)
				operation = BinaryExpression.Operation.Multiply;
			else if (op.Type == TokenType.OpSlash)
				operation = BinaryExpression.Operation.Divide;
			else if (op.Type == TokenType.OpPercentPercent)
				operation = BinaryExpression.Operation.DivisibleBy;
			else if (op.Type == TokenType.OpPercent)
				operation = BinaryExpression.Operation.Modulo;
			else
				throw new InvalidOperationException($"Unexpected multiplicative operation '{op.Text}'");
			
			expression = new BinaryExpression(expression, operation, op, right, range);
		}

		return expression;
	}

	private ExpressionNode ParseExponentiationExpression()
	{
		var expression = ParseSwitchWithExpression();

		if (Match(out var op, TokenType.OpStarStar)) 
		{
			var right = ParseExponentiationExpression();
			var range = expression.range.Join(right.range);
			expression = new BinaryExpression(expression, BinaryExpression.Operation.Power, op, right, range);
		}

		return expression;
	}

	private ExpressionNode ParseSwitchWithExpression()
	{
		/* Todo: Implement switch and with?
		if (Peek() == TokenType.KeywordSwitch)
		{
			return ParseSwitchExpression(tokens, ref position);
		}
		
		if (Peek() == TokenType.KeywordWith)
		{
			return ParseWithExpression(tokens, ref position);
		}*/
		
		return ParseRangeExpression();
	}

	private ExpressionNode ParseRangeExpression()
	{
		var expression = ParsePrefixUnaryExpression();

		while (Match(out var op, TokenType.OpDotDotEqual, TokenType.OpDotDot)) 
		{
			var right = ParsePrefixUnaryExpression();
			var range = expression.range.Join(right.range);
			
			BinaryExpression.Operation operation;
			if (op.Type == TokenType.OpDotDotEqual)
				operation = BinaryExpression.Operation.RangeInclusive;
			else if (op.Type == TokenType.OpDotDot)
				operation = BinaryExpression.Operation.RangeExclusive;
			else
				throw new InvalidOperationException($"Unexpected range operation '{op.Text}'");
			
			expression = new BinaryExpression(expression, operation, op, right, range);
		}

		return expression;
	}

	private static UnaryExpression.Operation? GetPrefixUnaryOperation(TokenType op)
	{
		if (op == TokenType.OpPlus)
			return UnaryExpression.Operation.Identity;
		
		if (op == TokenType.OpMinus)
			return UnaryExpression.Operation.Negate;
		
		if (op == TokenType.OpBang)
			return UnaryExpression.Operation.Not;
		
		/* Todo: Await
		if (op == TokenType.KeywordAwait)
			return UnaryExpression.Operation.Await;
		*/

		return null;
	}

	private static UnaryExpression.Operation? GetPostfixUnaryOperation(TokenType op)
	{
		// If postfix unary operators are added, they will be defined here
		return null;
	}

	private ExpressionNode ParsePrefixUnaryExpression()
	{
		if (TokenAt(position) is not { } op || GetPrefixUnaryOperation(op.Type) is not { } operation)
			return ParsePostfixUnaryExpression();

		position++;
		var right = ParsePrefixUnaryExpression();
		var range = op.Range.Join(right.range);
		
		if (right is not TokenExpression tokenExpression)
			return new UnaryExpression(right, operation, op, true, range);
		
		// Compile time negation of literals
		// Todo: Move to semantic analysis step -- probably shouldn't do this in the parser!
		// Todo: Handle unsigned types, fixed types, and float128 type
		var token = tokenExpression.token;
		if (token.Type == TokenType.NumberLiteral)
		{
			switch (operation)
			{
				case UnaryExpression.Operation.Identity:
					return tokenExpression;
					
				case UnaryExpression.Operation.Negate:
					switch (token.Value)
					{
						case sbyte value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, (sbyte)-value));
							
						case short value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, (short)-value));
							
						case int value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, -value));
							
						case long value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, -value));
						
						case float value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, -value));
						
						case double value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, -value));
					}
					break;
					
				case UnaryExpression.Operation.Not:
					switch (token.Value)
					{
						case sbyte value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, (sbyte)~value));
							
						case byte value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, (byte)~value));
							
						case short value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, (short)~value));
							
						case ushort value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, (ushort)~value));
							
						case int value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, ~value));
							
						case uint value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, ~value));
							
						case long value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, ~value));
							
						case ulong value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, ~value));
							
						case bool value:
							return new TokenExpression(new Token(token.Type, token.Range, token.Source, !value));
					}
					break;
			}
		}
		else if (token.Type == TokenType.KeywordTrue || token.Type == TokenType.KeywordFalse)
		{
			if (token.Value is bool value && operation == UnaryExpression.Operation.Not)
				return new TokenExpression(new Token(token.Type, token.Range, token.Source, !value));
		}

		return new UnaryExpression(right, operation, op, true, range);
	}
	
	private ExpressionNode ParsePostfixUnaryExpression()
	{
		var expression = ParsePrimaryExpression();

		while (true)
		{
			var next = Next();
			if (next?.Type is not { } peek)
				break;
			
			// Access
			if (peek == TokenType.OpQuestionDot || peek == TokenType.OpDot)
			{
				expression = ParseAccessExpression(expression);
				continue;
			}
			
			// Postfix unary operators
			if (GetPostfixUnaryOperation(peek) is { } operation)
			{
				var op = TokenAt(position)!;
				var endToken = tokens[position++];
				expression = new UnaryExpression(expression, operation, op, false,
					expression.range.Join(endToken.Range));
				
				continue;
			}
			
			// End chaining if newline and not one of the above
			if (next.IsAfterNewline)
				break;
			
			// Function call
			if (peek == TokenType.OpOpenParen)
			{
				expression = ParseFunctionCallExpression(expression);
				continue;
			}
			
			// Index
			if (peek == TokenType.OpQuestionOpenBracket || peek == TokenType.OpOpenBracket)
			{
				expression = ParseIndexExpression(expression);
				continue;
			}
			
			// Todo: Instantiation expression (should this be added at all?)
			/*if (peek == TokenType.OpLeftBrace)
			{
				expression = ParseInstantiationExpression(tokens, ref position, expression);
				continue;
			}*/

			break;
		}

		return expression;
	}

	private AccessExpression ParseAccessExpression(ExpressionNode source)
	{
		var accessOperator = Require(null, TokenType.OpQuestionDot, TokenType.OpDot);
		var nullCheck = accessOperator.Type == TokenType.OpQuestionDot;
		var target = ParsePrimaryExpression();

		return new AccessExpression(source, target, nullCheck, source.range.Join(target.range));
	}

	private IndexExpression ParseIndexExpression(ExpressionNode source)
	{
		var accessOperator = Require(null, TokenType.OpQuestionOpenBracket, TokenType.OpOpenBracket);
		var nullCheck = accessOperator.Type == TokenType.OpQuestionOpenBracket;
		var index = ParseExpression();
		var endToken = Require(null, TokenType.OpCloseBracket);

		return new IndexExpression(source, index, nullCheck, source.range.Join(endToken.Range));
	}

	private FunctionCallExpression ParseFunctionCallExpression(ExpressionNode caller)
	{
		Require(null, TokenType.OpOpenParen);
		var args = new List<ExpressionNode>();
		
		if (Peek() != TokenType.OpCloseParen)
			args = ParseArgumentList();
		
		var endToken = Require(null, TokenType.OpCloseParen);

		return new FunctionCallExpression(caller, args, caller.range.Join(endToken.Range));
	}

	private List<ExpressionNode> ParseArgumentList()
	{
		var args = new List<ExpressionNode>();
		
		do
		{
			args.Add(ParseExpression());
		} while (Match(TokenType.OpComma));

		return args;
	}
	
	private ExpressionNode ParsePrimaryExpression()
	{
		if (Match(out var literal, TokenType.NumberLiteral, TokenType.StringLiteral, TokenType.CharLiteral,
			    TokenType.KeywordSelf, TokenType.KeywordTrue, TokenType.KeywordFalse, TokenType.KeywordNull))
		{
			return new TokenExpression(literal);
		}

		var next = Next();
		if (next?.Type is not { } peek)
			throw new ParseException("Expected expression; Instead, got end of file", source, TextRange.Empty);
		
		if (peek == TokenType.InterpolatedStringLiteral)
			return ParseInterpolatedString();
			
		if (peek == TokenType.OpOpenParen)
			return ParseTupleExpression();

		if (peek == TokenType.OpOpenBracket)
			return ParseListExpression();

		if (peek == TokenType.Identifier)
			return new TokenExpression(Require(null, TokenType.Identifier));

		var start = position;
		try
		{
			return new TypeExpression(ParseSyntaxType());
		}
		catch
		{
			position = start;
			throw new ParseException($"Expected expression; Instead, got '{peek}'", next);
		}
	}

	private readonly struct InterpolationPart
	{
		public readonly string text;
		public readonly bool isStringLiteral;
		public readonly TextRange range;

		public InterpolationPart(string text, bool isStringLiteral, TextRange range)
		{
			this.text = text;
			this.isStringLiteral = isStringLiteral;
			this.range = range;
		}
	}
	
	private InterpolatedStringExpression ParseInterpolatedString()
	{
		var stringLiteral = Require(null, TokenType.InterpolatedStringLiteral);
		if (stringLiteral.Value is not string text)
			throw new ParseException("Malformed interpolated string", stringLiteral);
		
		var stringParts = new List<InterpolationPart>();

		var escaped = false;
		var withinString = true;
		var rangeStart = stringLiteral.Range.Start + 1;
		var start = 0;
		for (var i = 0; i < text.Length; i++)
		{
			var character = text[i];

			if (!withinString)
			{
				switch (character)
				{
					case '"':
						withinString = true;
						start = i + 1;
						break;
					case '}':
						withinString = true;
						
						var partText = text[start..i];
						if (partText != "")
							stringParts.Add(
								new InterpolationPart(partText, false, new TextRange(start, i) + rangeStart));
						
						start = i + 1;
						break;
				}

				continue;
			}
			
			switch (character)
			{
				case '\\':
					escaped = !escaped;
					continue;
				
				case '{' when !escaped:
				{
					var partText = text[start..i];
					if (partText != "")
						stringParts.Add(new InterpolationPart(partText, true, new TextRange(start, i) + rangeStart));
				
					start = i + 1;
					withinString = false;
					break;
				}
				
				case '"' when !escaped:
				{
					var partText = text[start..i];
					if (partText != "")
						stringParts.Add(new InterpolationPart(partText, true, new TextRange(start, i) + rangeStart));
				
					start = i + 1;
					withinString = false;
					break;
				}
			}

			escaped = false;
		}

		if (withinString)
		{
			var partText = text[start..];
			if (partText != "")
				stringParts.Add(new InterpolationPart(partText, true, new TextRange(start, text.Length) + rangeStart));
		}

		var parts = new List<ExpressionNode>();
		foreach (var part in stringParts)
		{
			if (part.isStringLiteral)
			{
				var value = Lexer.UnescapeString(part.text, true);
				var token = new Token(TokenType.StringLiteral, part.range, source, value);
				parts.Add(new TokenExpression(token));
			}
			else
			{
				var partSource = new StringBuffer(part.text);
				var lexer = new FilteredLexer(partSource);
				var interpolatedTokens = new List<Token>();

				foreach (var interpolatedToken in lexer)
				{
					if (interpolatedToken.Type.IsInvalid)
					{
						var characters = interpolatedToken.Text.Length > 1 ? "characters" : "character";
						throw new ParseException($"Unexpected {characters} in interpolated string", interpolatedToken);
					}

					interpolatedTokens.Add(new Token(interpolatedToken.Type, interpolatedToken.Range + part.range.Start,
						source, interpolatedToken.Value));
				}

				var interpolationParser = new Parser(partSource, interpolatedTokens);
				parts.Add(interpolationParser.ParseExpression());
			}
		}

		return new InterpolatedStringExpression(parts, stringLiteral.Range);
	}

	private ExpressionNode ParseTupleExpression()
	{
		var startToken = Require(null, TokenType.OpOpenParen);

		var expressions = new List<ExpressionNode>();
		do
		{
			expressions.Add(ParseExpression());
		} while (Match(TokenType.OpComma));
		
		var endToken = Require(null, TokenType.OpCloseParen);

		// If the tuple has only 1 value, it is actually a parenthesized expression, not a tuple
		if (expressions.Count == 1)
			return expressions[0];
		
		return new TupleExpression(expressions, startToken.Range.Join(endToken.Range));
	}

	private ExpressionNode ParseListExpression()
	{
		if (TryParseMapExpression(out var mapExpression))
			return mapExpression;
		
		var startToken = Require(null, TokenType.OpOpenBracket);

		var expressions = new List<ExpressionNode>();
		do
		{
			if (Peek() == TokenType.OpCloseBracket)
				break;
			
			expressions.Add(ParseExpression());
		} while (Match(TokenType.OpComma));
		
		var endToken = Require(null, TokenType.OpCloseBracket);
		var range = startToken.Range.Join(endToken.Range);
		
		if (!Match(TokenType.OpColon))
			return new ListExpression(expressions, null, range);
		
		var type = ParseSyntaxType();
		range = range.Join(type.range);

		return new ListExpression(expressions, type, range);
	}

	private bool TryParseMapExpression([NotNullWhen(true)] out MapExpression? mapExpression)
	{
		var start = position;
		try
		{
			mapExpression = ParseMapExpression();
			return true;
		}
		catch (ParseException)
		{
			position = start;
			mapExpression = null;
			return false;
		}
	}

	private MapExpression ParseMapExpression()
	{
		var startToken = Require(null, TokenType.OpOpenBracket);

		var expressions = new List<KeyValuePair<ExpressionNode, ExpressionNode>>();
		do
		{
			if (Peek() == TokenType.OpCloseBracket)
				break;
			
			var keyExpression = ParseExpression();
			Require(null, TokenType.OpColon);
			var valueExpression = ParseExpression();
			
			expressions.Add(new KeyValuePair<ExpressionNode, ExpressionNode>(keyExpression, valueExpression));
		} while (Match(TokenType.OpComma));
		
		var endToken = Require(null, TokenType.OpCloseBracket);
		var range = startToken.Range.Join(endToken.Range);
		
		if (!Match(TokenType.OpColon))
			return new MapExpression(expressions, null, range);
		
		var type = ParseTupleType();
		var tupleType = type as SyntaxType.Tuple;
		if (tupleType?.types.Length != 2)
			throw new ParseException("Map type must be a tuple of two types", source, type.range);
		
		return new MapExpression(expressions, tupleType, range.Join(type.range));
	}

	private SyntaxType ParseTupleType()
	{
		var types = new List<SyntaxType>();
		var startToken = Require(null, TokenType.OpOpenParen);

		do
		{
			types.Add(ParseSyntaxType());
		} while (Match(TokenType.OpComma));
		
		var endToken = Require(null, TokenType.OpCloseParen);

		// If the tuple has only 1 value, it is actually a parenthesized type, not a tuple
		if (types.Count == 1)
			return types[0];

		return new SyntaxType.Tuple(types, startToken.Range.Join(endToken.Range));
	}
}