using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class FileParser(ImmutableArray<Token> tokens) : BaseParser<FileNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _topLevelContextualKeywords = new()
	{
		{ TokenType.KeywordMod.Representation, TokenType.KeywordMod },
		{ TokenType.KeywordEntry.Representation, TokenType.KeywordEntry }
	};
	
	private static readonly HashSet<TokenType> _topLevelSyncTypes = [TokenType.OpSemicolon, TokenType.EndOfFile];
	
	public override FileNode? Parse(ref int index)
	{
		if (Tokens.Length == 0)
			// @TODO Diagnostic
			return null;
		
		// Setup
		var (source, range) = Tokens[0].SourceLocation;
		
		string? moduleName = null;
		var nodes = new List<ISyntaxNode>(); // @TODO Change to declaration nodes?
		
		while (!AtEnd(index))
		{
			// Parse module name
			if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordMod))
			{
				if (!Consume(ref index, out var modIdentifier, _topLevelSyncTypes, TokenType.Identifier))
				{
					// @TODO Diagnostic: Expected identifier
					continue;
				}
				
				if (!Consume(ref index, _topLevelSyncTypes, TokenType.OpSemicolon))
				{
					// @TODO Diagnostic: Expected ';'
					continue;
				}
				
				//if (moduleName is not null)
				// @TODO Diagnostic: Module name already defined in this file
				
				moduleName = modIdentifier.GetText();
				continue;
			}
			
			// Parse top-level declarations
			if (Match(ref index, out var identifier, _topLevelContextualKeywords, TokenType.Identifier))
			{
				if (!Consume(ref index, _topLevelSyncTypes, TokenType.OpColon))
				{
					// @TODO Diagnostic: Expected identifier
					continue;
				}
				
				// Entry point function
				if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordEntry))
				{
					if (ParseFunction(ref index, identifier) is not { } function)
					{
						ResyncTopLevel(ref index);
						continue;
					}
					
					var entryPoint = new EntryPointNode(identifier.SourceLocation, function);
					nodes.Add(entryPoint);
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
		
		if (moduleName is null)
			// @TODO Diagnostic: File must have a module name
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
			ModuleName = moduleName,
			Nodes = nodes.ToImmutableArray()
		};
	}
	
	private FunctionNode? ParseFunction(ref int index, Token identifier)
	{
		// When this is called, the identifier and fun/entry keywords are already consumed
		// Caller is expected to resync in case of errors
		
		// @TODO Diagnostics
		
		if (!Match(ref index, TokenType.OpOpenParen))
			return null;
		
		// @TODO Parameter list
		
		if (!Match(ref index, TokenType.OpCloseParen))
			return null;
		
		// @TODO Optional return type
		
		if (!Match(ref index, TokenType.OpColon))
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
		
		if (ParseExpression(ref index) is not { } expression)
			return null;
		
		if (!Match(ref index, out var semicolon, TokenType.OpSemicolon))
			return null;
		
		var range = retToken.SourceLocation.Range.Join(semicolon.SourceLocation.Range);
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