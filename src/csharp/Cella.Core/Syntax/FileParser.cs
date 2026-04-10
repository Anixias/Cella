using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class FileParser(ImmutableArray<Token> tokens, string fileName) : BaseParser<FileNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _topLevelContextualKeywords =
		BuildContextualKeywords(TokenType.KeywordMod, TokenType.KeywordFun, TokenType.KeywordUse, TokenType.KeywordPub,
			TokenType.KeywordExt);
	
	private static readonly HashSet<TokenType> _topLevelSyncTypes = [TokenType.OpSemicolon, TokenType.EndOfFile];
	
	private static Dictionary<string, TokenType> BuildContextualKeywords(params IEnumerable<TokenType> tokenTypes) =>
		tokenTypes.ToDictionary(static t => t.Representation);
	
	public override FileNode? Parse(ref int index)
	{
		if (Tokens.Length == 0)
			// @TODO Diagnostic
			return null;
		
		// Setup
		var (source, range) = Tokens[0].SourceLocation;
		
		ModuleName moduleName = default;
		var declarations = new List<IDeclarationNode>();
		var imports = new List<ImportExpression>();
		
		var moduleNameAllowed = true;
		var importsAllowed = false;
		
		while (!AtEnd(index))
		{
			// Parse module name
			if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordMod))
			{
				try
				{
					moduleName = ParseModuleName(ref index);
				}
				catch (Exception e)
				{
					// TODO Diagnostics
				}
				
				if (moduleNameAllowed)
				{
					moduleNameAllowed = false;
					importsAllowed = true;
				}
				// TODO else Diagnostics
			}
			
			// Parse imports
			if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordUse))
			{
				imports.Add(ParseImportExpression(ref index));
				
				// TODO if (!importsAllowed) Diagnostics
				
				continue;
			}
			
			// Parse external declarations
			if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordExt))
			{
				importsAllowed = false;
				declarations.AddRange(ParseExternalDeclarations(ref index).Where(static ed => ed is not null)!);
				continue;
			}
			
			// Parse top-level declarations
			if (Match(ref index, out var identifier, _topLevelContextualKeywords, TokenType.Identifier))
			{
				importsAllowed = false;
				
				if (!Consume(ref index, _topLevelSyncTypes, TokenType.OpColon))
				{
					// @TODO Diagnostic: Expected identifier
					continue;
				}
				
				var modifiers = ParseDeclarationModifiers(ref index);
				
				// Function
				if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordFun))
				{
					if (ParseFunction(ref index, identifier, modifiers) is not { } function)
					{
						ResyncTopLevel(ref index);
						continue;
					}
					
					declarations.Add(function);
					continue;
				}
				
				// Unknown declaration, cannot resync
				// @TODO Emit error
				break;
			}
			
			// Unexpected token, cannot resync
			// @TODO Emit error
			break;
		}
		
		if (moduleName == default)
			// @TODO Diagnostic: File must have a module name -> Semantic analysis
			return null;
		
		// Combine range to include penultimate token (because last must be EOF)
		if (index > 1)
			range.Join(Tokens[index - 1].SourceLocation.Range);
		
		// Must read EOF at end
		if (!Match(ref index, TokenType.EndOfFile))
			// @TODO Diagnostic: Error during parsing
			return null;
		
		return new FileNode(new(source, range))
		{
			FileName = fileName,
			ModuleName = moduleName,
			Imports = imports.ToImmutableArray(),
			Declarations = declarations.ToImmutableArray()
		};
	}
	
	private List<Token> ParseDeclarationModifiers(ref int index)
	{
		var modifiers = new List<Token>();
		
		if (Match(ref index, out var pubToken, _topLevelContextualKeywords, TokenType.KeywordPub))
			modifiers.Add(pubToken);
		
		return modifiers;
	}
	
	private List<IDeclarationNode?> ParseExternalDeclarations(ref int index)
	{
		// 'ext' token already consumed by caller
		
		// TODO Diagnostics
		
		// Optional library origin
		string? origin;
		if (Match(ref index, TokenType.OpOpenParen))
		{
			if (!Match(ref index, out var str, TokenType.StringLiteral))
				return [];
			
			origin = str.Text;
			
			if (!Match(ref index, TokenType.OpCloseParen))
				return [];
		}
		else
			origin = null;
		
		if (!Match(ref index, TokenType.OpOpenBrace))
			return [ParseExternalDeclaration(ref index, origin)];
		
		var nodes = new List<IDeclarationNode?>();
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (ParseExternalDeclaration(ref index, origin) is not { } ext)
			{
				// TODO Diagnostics
				nodes.Add(null);
				SkipUntil(ref index, TokenType.OpCloseBrace);
				break;
			}
			
			nodes.Add(ext);
		}
		
		return nodes;
	}
	
	private ModuleName ParseModuleName(ref int index)
	{
		var parts = new List<Token>();
		
		if (!Consume(ref index, out var firstPart, _topLevelSyncTypes, TokenType.Identifier))
			throw new Exception(); // TODO
		
		parts.Add(firstPart);
		
		while (Match(ref index, TokenType.OpDot))
		{
			if (Match(ref index, out var part, TokenType.Identifier))
				parts.Add(part);
		}
		
		return new(parts.ToImmutableArray());
	}
	
	private ImportExpression ParseImportExpression(ref int index)
	{
		if (!Consume(ref index, out var firstPart, _topLevelSyncTypes, TokenType.Identifier))
			throw new Exception(); // TODO
		
		var parts = new List<Token> { firstPart };
		
		IImport? import = null;
		while (Match(ref index, TokenType.OpDot))
		{
			if (Match(ref index, out var part, TokenType.Identifier))
			{
				parts.Add(part);
				continue;
			}
			
			if (Match(ref index, TokenType.OpOpenBracket))
			{
				// Multiple import tokens
				if (!Consume(ref index, out var firstImport, _topLevelSyncTypes, TokenType.Identifier))
					throw new Exception(); // TODO
				
				var importTokens = new List<Token> { firstImport };
				
				while (Match(ref index, TokenType.OpComma))
				{
					if (!Match(ref index, out var importToken, TokenType.Identifier))
						throw new Exception(); // TODO
					
					importTokens.Add(importToken);
				}
				
				if (!Match(ref index, TokenType.OpCloseBracket))
					throw new Exception(); // TODO Diagnostics
				
				import = new ListImport(importTokens.ToImmutableArray());
				continue;
			}
			
			if (!Match(ref index, TokenType.OpStar))
				throw new Exception(); // TODO
			
			import = FullImport.Instance;
		}
		
		if (import is null)
		{
			if (parts.Count < 2)
				throw new Exception(); // TODO Diagnostics
			
			import = new TokenImport(parts[^1]);
			parts.RemoveAt(parts.Count - 1);
		}
		
		return new(new(parts.ToImmutableArray()), import);
	}
	
	private FunctionNode? ParseFunction(ref int index, Token identifier, IEnumerable<Token> modifiers)
	{
		// When this is called, the identifier and fun keyword are already consumed
		// Caller is expected to resync in case of errors
		
		// @TODO Diagnostics
		
		if (ParseFunctionSignature(ref index) is not { } signature)
			return null;
		
		var (parameters, returnType) = signature;
		
		if (Match(ref index, TokenType.OpEqual))
		{
			var expression = ParseExpression(ref index);
			var statement = new ReturnStatementNode(expression.SourceLocation, expression);
			return new(identifier, modifiers, parameters, returnType, statement);
		}
		
		if (!Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
			return null;
		
		if (ParseBlockStatement(ref index, openBraceToken) is not { } body)
			return null;
		
		return new(identifier, modifiers, parameters, returnType, body);
	}
	
	private ExternalFunctionNode? ParseExternalDeclaration(ref int index, string? origin)
	{
		// TODO Diagnostics
		
		if (!Match(ref index, out var identifier, _topLevelContextualKeywords, TokenType.Identifier))
			return null;
		
		if (!Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseDeclarationModifiers(ref index);
		
		if (!Match(ref index, _topLevelContextualKeywords, TokenType.KeywordFun))
			return null;
		
		if (ParseFunctionSignature(ref index) is not { } signature)
			return null;
		
		var (parameters, returnType) = signature;
		return new(identifier, modifiers, parameters, returnType, origin);
	}
	
	private (List<ParameterNode> Parameters, ITypeNode? ReturnType)? ParseFunctionSignature(ref int index)
	{
		var parameters = new List<ParameterNode>();
		
		// Parentheses are optional for function declarations
		if (Match(ref index, TokenType.OpOpenParen))
		{
			if (!Match(ref index, TokenType.OpCloseParen))
			{
				if (ParseParameterList(ref index) is not { } parameterList)
					return null;
				
				parameters.AddRange(parameterList);
				
				// TODO Diagnostics
				if (!Match(ref index, TokenType.OpCloseParen))
					return null;
			}
		}
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
		return (parameters, returnType);
	}
	
	private List<ParameterNode>? ParseParameterList(ref int index)
	{
		var result = new List<ParameterNode>();
		
		if (ParseParameter(ref index) is not { } firstParameter)
			return null;
		
		result.Add(firstParameter);
		
		while (Match(ref index, TokenType.OpComma))
		{
			if (ParseParameter(ref index) is not { } parameter)
				return null;
			
			result.Add(parameter);
		}
		
		return result;
	}
	
	private ParameterNode? ParseParameter(ref int index)
	{
		// TODO Diagnostics
		
		// TODO Attempt resync 
		if (!Match(ref index, out var identifier, TokenType.Identifier))
			return null;
		
		// TODO Potentially make type optional, inferred from default or make auto-generic?
		if (!Match(ref index, TokenType.OpColon))
			return null;
		
		// TODO Type should be more complex than a simple identifier token
		var type = ParseType(ref index);
		
		if (!Match(ref index, TokenType.OpEqual))
			return new(identifier, type, null);
		
		var defaultValue = ParseExpression(ref index);
		return new(identifier, type, defaultValue);
	}
	
	private BlockStatementNode? ParseBlockStatement(ref int index, Token open)
	{
		// @TODO Diagnostics
		
		// @TODO Replace with statement node type
		var statements = new List<IStatementNode>();
		
		Token close;
		while (!Match(ref index, out close, TokenType.OpCloseBrace))
		{
			if (ParseStatement(ref index) is not { } statement)
			{
				ResyncSimple(ref index);
				return null;
			}
			
			if (AtEnd(index))
				return null;
			
			statements.Add(statement);
		}
		
		var source = open.SourceLocation.Source;
		var range = open.SourceLocation.Range.Join(close.SourceLocation.Range);
		var sourceLocation = new SourceLocation(source, range);
		return new(sourceLocation, statements.ToImmutableArray());
	}
	
	private IStatementNode? ParseStatement(ref int index)
	{
		// @TODO Diagnostics
		
		if (Match(ref index, out var varToken, TokenType.KeywordVar))
			return ParseVarStatement(ref index, varToken);
		
		if (Match(ref index, out var ifToken, TokenType.KeywordIf))
			return ParseIfStatement(ref index, ifToken);
		
		if (Match(ref index, out var forToken, TokenType.KeywordFor))
			return ParseForStatement(ref index, forToken);
		
		if (Match(ref index, out var loopToken, TokenType.KeywordLoop))
			return ParseLoopStatement(ref index, loopToken);
		
		if (Match(ref index, out var retToken, TokenType.KeywordRet))
			return ParseReturnStatement(ref index, retToken);
		
		if (Match(ref index, out var breakToken, TokenType.KeywordBreak))
			return ParseBreakStatement(ref index, breakToken);
		
		if (Match(ref index, out var contToken, TokenType.KeywordCont))
			return ParseContinueStatement(ref index, contToken);
		
		if (Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
			return ParseBlockStatement(ref index, openBraceToken);
		
		// Named loops
		var expr = ParseExpression(ref index);
		if (expr is not VarExpressionNode var)
			return new ExpressionStatementNode(expr);
		
		// Attempt to parse as loop, else backtrack
		var backtrackIndex = index;
		
		if (Match(ref index, TokenType.OpColon))
		{
			if (Match(ref index, out var namedForToken, TokenType.KeywordFor))
				return ParseForStatement(ref index, namedForToken, var.Identifier);
			
			if (Match(ref index, out var namedLoopToken, TokenType.KeywordLoop))
				return ParseLoopStatement(ref index, namedLoopToken, var.Identifier);
		}
		
		index = backtrackIndex;
		return new ExpressionStatementNode(expr);
	}
	
	private IStatementNode? ParseForStatement(ref int index, Token forToken, Token? labelToken = null)
	{
		throw new NotImplementedException();
	}
	
	private IStatementNode? ParseLoopStatement(ref int index, Token loopToken, Token? labelToken = null)
	{
		// TODO Diagnostics
		
		var (source, range) = loopToken.SourceLocation;
		if (labelToken is { } label)
			range = range.Join(label.SourceLocation.Range);
		
		// While-loop:
		if (Match(ref index, TokenType.KeywordWhile))
		{
			var condition = ParseExpression(ref index);
			
			if (ParseStatement(ref index) is not { } body)
				return null;
			
			range = range.Join(body.SourceLocation.Range);
			return new WhileStatementNode(new(source, range), condition, body, labelToken);
		}
		
		// Repeat loop:
		if (Match(ref index, TokenType.KeywordFor))
		{
			var count = ParseExpression(ref index);
			
			if (ParseStatement(ref index) is not { } body)
				return null;
			
			range = range.Join(body.SourceLocation.Range);
			return new RepeatStatementNode(new(source, range), count, body, labelToken);
		}
		
		// Other loops
		if (ParseStatement(ref index) is not { } statement)
			return null;
		
		// Do-While loop:
		if (Match(ref index, TokenType.KeywordWhile))
		{
			var condition = ParseExpression(ref index);
			
			range = range.Join(condition.SourceLocation.Range);
			return new DoWhileStatementNode(new(source, range), statement, condition, labelToken);
		}
		
		// Infinite loop:
		range = range.Join(statement.SourceLocation.Range);
		return new LoopStatementNode(new(source, range), statement, labelToken);
	}
	
	private IfStatementNode? ParseIfStatement(ref int index, Token ifToken)
	{
		// ifToken already consumed when this function is called
		
		// TODO Diagnostics
		
		var condition = ParseExpression(ref index);
		
		if (ParseStatement(ref index) is not { } then)
			return null;
		
		var (source, range) = ifToken.SourceLocation;
		if (!Match(ref index, TokenType.KeywordElse))
		{
			range = range.Join(then.SourceLocation.Range);
			return new(new(source, range), condition, then, null);
		}
		
		if (ParseStatement(ref index) is not { } @else)
			return null;
		
		range = range.Join(@else.SourceLocation.Range);
		return new(new(source, range), condition, then, @else);
	}
	
	private VarStatementNode? ParseVarStatement(ref int index, Token varToken)
	{
		// varToken already consumed when this function is called
		
		// TODO Diagnostics
		
		// TODO Attempt resync 
		if (!Match(ref index, out var identifier, TokenType.Identifier))
			return null;
		
		var type = Match(ref index, TokenType.OpColon) ? ParseType(ref index) : null;
		
		var (source, range) = varToken.SourceLocation;
		
		if (!Match(ref index, TokenType.OpEqual))
			return new(new(source, range), identifier, type, null);
		
		var initializer = ParseExpression(ref index);
		range = range.Join(initializer.SourceLocation.Range);
		return new(new(source, range), identifier, type, initializer);
	}
	
	private ReturnStatementNode ParseReturnStatement(ref int index, Token retToken)
	{
		// retToken already consumed when this function is called
		
		// @TODO Diagnostics
		var range = retToken.SourceLocation.Range;
		var source = retToken.SourceLocation.Source;
		
		if (AtEnd(index) || retToken.Line != Tokens[index].Line || TryParseExpression(ref index) is not { } expression)
			return new(new(source, range), null);
		
		range = range.Join(expression.SourceLocation.Range);
		return new(new(source, range), expression);
	}
	
	private BreakStatementNode ParseBreakStatement(ref int index, Token breakToken)
	{
		// breakToken already consumed when this function is called
		
		// @TODO Diagnostics
		var range = breakToken.SourceLocation.Range;
		var source = breakToken.SourceLocation.Source;
		
		if (AtEnd(index) || breakToken.Line != Tokens[index].Line ||
		    TryParseExpression(ref index) is not { } expression)
			return new(new(source, range), null);
		
		range = range.Join(expression.SourceLocation.Range);
		return new(new(source, range), expression);
	}
	
	private ContinueStatementNode ParseContinueStatement(ref int index, Token contToken)
	{
		// continueToken already consumed when this function is called
		
		// @TODO Diagnostics
		var range = contToken.SourceLocation.Range;
		var source = contToken.SourceLocation.Source;
		
		if (AtEnd(index) || contToken.Line != Tokens[index].Line || TryParseExpression(ref index) is not { } expression)
			return new(new(source, range), null);
		
		range = range.Join(expression.SourceLocation.Range);
		return new(new(source, range), expression);
	}
	
	private IExpressionNode ParseExpression(ref int index) => new ExpressionParser(Tokens).Parse(ref index);
	private ITypeNode ParseType(ref int index) => new TypeParser(Tokens).Parse(ref index);
	
	private IExpressionNode? TryParseExpression(ref int index)
	{
		try
		{
			var parserIndex = index;
			var result = ParseExpression(ref parserIndex);
			index = parserIndex;
			return result;
		}
		catch
		{
			return null;
		}
	}
	
	private void ResyncTopLevel(ref int index)
	{
		SkipUntil(ref index, _topLevelSyncTypes);
		SkipIf(ref index, TokenType.OpSemicolon);
	}
	
	private void ResyncSimple(ref int index)
	{
		SkipUntil(ref index, TokenType.OpSemicolon);
		SkipIf(ref index, TokenType.OpSemicolon);
	}
	
	private void SkipIf(ref int index, TokenType type)
	{
		if (!AtEnd(index) && Tokens[index].Type == type)
			index++;
	}
}