using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class TypeParser(ImmutableArray<Token> tokens) : BaseParser<ITypeNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _functionKeywords =
		new[] { TokenType.KeywordExt, TokenType.KeywordFun }.ToDictionary(static t => t.Representation);
	
	public override ITypeNode Parse(ref int index) => ParseType(ref index);
	
	private ITypeNode ParseType(ref int index)
	{
		if (TryParseFunctionType(ref index) is { } functionType)
			return functionType;
		
		// TEMP Should emit an erroneous node instead
		if (!Match(ref index, out var identifier, TokenType.Identifier))
			throw new InvalidOperationException();
		
		var startIndex = index;
		if (!Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			return new IdentifierTypeNode(identifier);
		
		// Disallow the open bracket being on another line from the identifier; backtrack to unconsume open bracket
		if (openBracket.Line != identifier.Line)
		{
			index = startIndex;
			return new IdentifierTypeNode(identifier);
		}
		
		var args = new List<IGenericArgumentNode> { ParseGenericArgument(ref index) };
		while (Match(ref index, TokenType.OpComma))
			args.Add(ParseGenericArgument(ref index));
		
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw new InvalidOperationException();
		
		var (source, range) = identifier.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		
		return new GenericTypeNode(new(source, range), identifier, args);
	}
	
	private FunctionTypeNode? TryParseFunctionType(ref int index)
	{
		var start = index;
		if (!Match(ref index, out var ext, _functionKeywords, TokenType.KeywordExt) ||
		    !Match(ref index, out var end, _functionKeywords, TokenType.KeywordFun))
		{
			index = start;
			return null;
		}
		
		var parameterTypes = new List<ITypeNode>();
		if (Match(ref index, TokenType.OpOpenParen) && !Match(ref index, out end, TokenType.OpCloseParen))
		{
			parameterTypes.Add(ParseType(ref index));
			while (Match(ref index, TokenType.OpComma))
				parameterTypes.Add(ParseType(ref index));
			
			if (!Match(ref index, out end, TokenType.OpCloseParen))
				throw new InvalidOperationException();
		}
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
		var (source, range) = ext.SourceLocation;
		range = range.Join((returnType?.SourceLocation ?? end.SourceLocation).Range);
		return new FunctionTypeNode(new(source, range), true, parameterTypes, returnType);
	}
	
	private IGenericArgumentNode ParseGenericArgument(ref int index)
	{
		if (TryParseFunctionType(ref index) is { } functionType)
			return new TypeArgumentNode(functionType);
		
		if (TryParseExpression(ref index) is not { } expression)
			return new TypeArgumentNode(ParseType(ref index));
		
		if (expression is VarExpressionNode var)
			return new IdentifierArgumentNode(var.Identifier);
		
		return new ExpressionArgumentNode(expression);
	}
	
	private IExpressionNode ParseExpression(ref int index) => new ExpressionParser(Tokens).Parse(ref index);
	
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
}