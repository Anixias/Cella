using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class TypeParser(ImmutableArray<Token> tokens) : BaseParser<ITypeNode>(tokens)
{
	public override ITypeNode Parse(ref int index) => ParseType(ref index);
	
	private ITypeNode ParseType(ref int index)
	{
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
	
	private IGenericArgumentNode ParseGenericArgument(ref int index)
	{
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