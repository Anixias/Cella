using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Syntax.Nodes.Declarations;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class FileParser(ImmutableArray<Token> tokens, string fileName) : BaseParser<FileNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _topLevelContextualKeywords =
		BuildContextualKeywords(TokenType.KeywordMod, TokenType.KeywordFun, TokenType.KeywordUse);
	
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
				
				continue;
			}
			
			// Parse imports
			if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordUse))
				imports.Add(ParseImportExpression(ref index));
			
			// Parse top-level declarations
			if (Match(ref index, out var identifier, _topLevelContextualKeywords, TokenType.Identifier))
			{
				if (!Consume(ref index, _topLevelSyncTypes, TokenType.OpColon))
				{
					// @TODO Diagnostic: Expected identifier
					continue;
				}
				
				// Function
				if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordFun))
				{
					if (ParseFunction(ref index, identifier) is not { } function)
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
	
	private FunctionNode? ParseFunction(ref int index, Token identifier)
	{
		// When this is called, the identifier and fun/entry keywords are already consumed
		// Caller is expected to resync in case of errors
		
		// @TODO Diagnostics
		
		// Parentheses are optional for function declarations
		if (Match(ref index, TokenType.OpOpenParen))
		{
			// @TODO Parameter list
			
			if (!Match(ref index, TokenType.OpCloseParen))
				return null;
		}
		
		// @TODO Optional return type
		
		if (!Match(ref index, TokenType.OpArrow))
			return null;
		
		// @TODO Return type should be more complex than a simple identifier token
		if (!Match(ref index, out var returnType, TokenType.Identifier))
			return null;
		
		if (ParseBlock(ref index) is not { } body)
			return null;
		
		return new(identifier, returnType, body);
	}
	
	private BlockStatementNode? ParseBlock(ref int index)
	{
		// @TODO Diagnostics
		
		if (!Match(ref index, out var open, TokenType.OpOpenBrace))
			return null;
		
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
		
		// Return statement
		if (Match(ref index, out var ret, TokenType.KeywordRet))
			return ParseReturnStatement(ref index, ret);
		
		return null;
	}
	
	private ReturnStatementNode? ParseReturnStatement(ref int index, Token retToken)
	{
		// Ret token already consume when this function is called
		
		// @TODO Diagnostics
		
		var expressionIndex = index;
		var expression = ParseExpression(ref expressionIndex);
		
		var range = retToken.SourceLocation.Range;
		if (expression is not null)
		{
			index = expressionIndex;
			range = range.Join(expression.SourceLocation.Range);
		}
		
		var source = retToken.SourceLocation.Source;
		var sourceLocation = new SourceLocation(source, range);
		
		return new(sourceLocation, expression);
	}
	
	private IExpressionNode? ParseExpression(ref int index) => new ExpressionParser(Tokens).Parse(ref index);
	
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