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
	
	private static readonly HashSet<TokenType> _assignmentOps =
	[
		TokenType.OpPlusEqual,
		TokenType.OpMinusEqual,
		TokenType.OpStarEqual,
		TokenType.OpSlashEqual,
		TokenType.OpEqual
	];
	
	public override IExpressionNode Parse(ref int index) => ParseExpression(ref index);
	
	private IExpressionNode ParseExpression(ref int index)
	{
		var node = ParseAdditive(ref index);
		if (Match(ref index, out var op, _assignmentOps))
		{
			var right = ParseExpression(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
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
		// Parenthesized Expression
		if (Match(ref index, TokenType.OpOpenParen))
		{
			var node = ParseExpression(ref index);
			
			if (!Match(ref index, TokenType.OpCloseParen))
			{
				// TODO Diagnostics
			}
			
			return node;
		}
		
		// Call Expression & Variable Expression
		// TODO Contextual keywords
		// TODO Should not use identifier but instead a tightly-bound indexer, which is also used for type parameters
		if (Match(ref index, out var identifier, TokenType.Identifier))
		{
			// Call Expression
			if (Match(ref index, TokenType.OpOpenParen))
				return ParseCallExpression(ref index, identifier);
			
			// Variable Expression
			return new VarExpressionNode(identifier);
		}
		
		return ParseLiteral(ref index);
	}
	
	private IExpressionNode ParseCallExpression(ref int index, Token identifier)
	{
		// Caller already consumed open parenthesis
		var range = identifier.SourceLocation.Range;
		var arguments = new List<IExpressionNode>();
		
		Token closeParen;
		while (!Match(ref index, out closeParen, TokenType.OpCloseParen))
		{
			if (AtEnd(index))
			{
				// TODO Diagnostic
				range = range with { End = identifier.SourceLocation.Source.Length };
				break;
			}
			
			if (arguments.Count > 0 && !Match(ref index, TokenType.OpComma))
			{
				// TODO Diagnostic: Missing comma
			}
			
			arguments.Add(ParseExpression(ref index));
		}
		
		if (closeParen != default)
			range = range.Join(closeParen.SourceLocation.Range);
		
		return new CallExpressionNode(identifier, arguments, identifier.SourceLocation with { Range = range });
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