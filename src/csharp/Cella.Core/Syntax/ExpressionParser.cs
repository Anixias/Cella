using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class ExpressionParser(ImmutableArray<Token> tokens) : BaseParser<IExpressionNode>(tokens)
{
	private static readonly HashSet<TokenType> _syncTypes =
	[
		TokenType.OpOpenParen
	];
	
	private static readonly HashSet<TokenType> _literalTypes =
	[
		TokenType.IntegerLiteral
	];
	
	private static readonly HashSet<TokenType> _additiveOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus
	];
	
	private static readonly HashSet<TokenType> _unaryPrefixOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus
	];
	
	private static readonly HashSet<TokenType> _multiplicativeOps =
	[
		TokenType.OpStar,
		TokenType.OpSlash
	];
	
	public override IExpressionNode? Parse(ref int index)
	{
		return ParseExpression(ref index);
		
		// @TEMP For now, assume the only valid expression is an integer literal
		if (Match(ref index, out var literal, _literalTypes))
			return new LiteralExpressionNode(literal);
		
		// @TODO Diagnostics
		return null;
	}
	
	private IExpressionNode ParseExpression(ref int index) => ParseAdditive(ref index);
	
	private IExpressionNode ParseAdditive(ref int index)
	{
		var node = ParseMultiplicative(ref index);
		while (Match(ref index, out var op, _additiveOps))
		{
			var right = ParseMultiplicative(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseMultiplicative(ref int index)
	{
		var node = ParseUnary(ref index);
		while (Match(ref index, out var op, _multiplicativeOps))
		{
			var right = ParseUnary(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseUnary(ref int index)
	{
		if (!Match(ref index, out var op, _unaryPrefixOps))
			return ParsePrimary(ref index);
		
		var operand = ParseUnary(ref index);
		return new UnaryOpExpressionNode(op, operand);
		
	}
	
	private IExpressionNode ParsePrimary(ref int index)
	{
		if (!Match(ref index, TokenType.OpOpenParen))
			return ParseLiteral(ref index);
		
		var node = ParseExpression(ref index);
		
		if (!Match(ref index, TokenType.OpCloseParen))
		{
			// TODO Diagnostics
		}
		
		return node;
	}
	
	private IExpressionNode ParseLiteral(ref int index)
	{
		if (Match(ref index, out var literal, _literalTypes))
			return new LiteralExpressionNode(literal);
		
		// TODO Diagnostics
		// TEMP Should emit an erroneous node instead of throwing
		throw new InvalidOperationException();
	}
}