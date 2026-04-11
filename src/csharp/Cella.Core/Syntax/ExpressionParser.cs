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
		TokenType.KeywordTrue,
		TokenType.KeywordFalse,
		TokenType.IntegerLiteral,
		TokenType.StringLiteral
	];
	
	private static readonly HashSet<TokenType> _additiveOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus
	];
	
	private static readonly HashSet<TokenType> _unaryPrefixOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus,
		TokenType.OpBang
	];
	
	private static readonly HashSet<TokenType> _multiplicativeOps =
	[
		TokenType.OpStar,
		TokenType.OpSlash,
		TokenType.OpPercent,
	];
	
	private static readonly HashSet<TokenType> _assignmentOps =
	[
		TokenType.OpPlusEqual,
		TokenType.OpMinusEqual,
		TokenType.OpStarEqual,
		TokenType.OpSlashEqual,
		TokenType.OpPercentEqual,
		TokenType.OpAmpersandEqual,
		TokenType.OpBarEqual,
		TokenType.OpHatEqual,
		TokenType.OpEqual
	];
	
	private static readonly HashSet<TokenType> _equalityOps =
	[
		TokenType.OpEqualEqual,
		TokenType.OpBangEqual
	];
	
	private static readonly HashSet<TokenType> _comparisonOps =
	[
		TokenType.OpGreaterEqual,
		TokenType.OpLessEqual,
		TokenType.OpGreater,
		TokenType.OpLess
	];
	
	private bool IsNextNewline(int next)
	{
		if (AtEnd(next - 1) || AtEnd(next))
			return false;
		
		return Tokens[next - 1].Line != Tokens[next].Line;
	}
	
	public override IExpressionNode Parse(ref int index) => ParseExpression(ref index);
	private IExpressionNode ParseExpression(ref int index) => ParseAssignment(ref index);
	
	private IExpressionNode ParseAssignment(ref int index)
	{
		var left = ParseXor(ref index);
		if (!Match(ref index, out var op, _assignmentOps))
			return left;
		
		var right = ParseExpression(ref index);
		return new BinaryOpExpressionNode(left, op, right);
	}
	
	private IExpressionNode ParseXor(ref int index)
	{
		var node = ParseOr(ref index);
		while (Match(ref index, out var op, TokenType.OpHat))
		{
			var right = ParseOr(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseOr(ref int index)
	{
		var node = ParseAnd(ref index);
		while (Match(ref index, out var op, TokenType.OpBar))
		{
			var right = ParseAnd(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseAnd(ref int index)
	{
		var node = ParseEquality(ref index);
		while (Match(ref index, out var op, TokenType.OpAmpersand))
		{
			var right = ParseEquality(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseEquality(ref int index)
	{
		var node = ParseComparison(ref index);
		while (Match(ref index, out var op, _equalityOps))
		{
			var right = ParseComparison(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseComparison(ref int index)
	{
		var operands = new List<IExpressionNode> { ParseAdditive(ref index) };
		var ops = new List<Token>();
		
		while (Match(ref index, out var op, _comparisonOps))
		{
			ops.Add(op);
			operands.Add(ParseAdditive(ref index));
		}
		
		return operands.Count switch
		{
			1 => operands[0],
			2 => new BinaryOpExpressionNode(operands[0], ops[0], operands[1]),
			_ => new ChainedExpressionNode(operands, ops)
		};
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
		IExpressionNode node;
		
		if (Match(ref index, TokenType.OpOpenParen))
		{
			// Parenthesized Expression
			node = ParseExpression(ref index);
			
			if (!Match(ref index, TokenType.OpCloseParen))
			{
				// TODO Diagnostics
			}
		}
		else if (Match(ref index, out var openBracket, TokenType.OpOpenBracket))
		{
			// Array Expression
			var (source, range) = openBracket.SourceLocation;
			
			if (Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			{
				range = range.Join(closeBracket.SourceLocation.Range);
				node = new ArrayExpressionNode([], new(source, range));
			}
			else
			{
				var values = new List<IExpressionNode> { ParseExpression(ref index) };
				
				// TODO Allow trailing comma
				while (Match(ref index, TokenType.OpComma))
					values.Add(ParseExpression(ref index));
				
				if (!Match(ref index, out closeBracket, TokenType.OpCloseBracket))
				{
					// TODO Diagnostics
					throw new InvalidOperationException();
				}
				
				range = range.Join(closeBracket.SourceLocation.Range);
				node = new ArrayExpressionNode(values, new(source, range));
			}
		}
		else if (Match(ref index, out var identifier, TokenType.Identifier))
			node = new VarExpressionNode(identifier);
		else
			node = ParseLiteral(ref index);
		
		return ParsePostfix(ref index, node);
	}
	
	private IExpressionNode ParsePostfix(ref int index, IExpressionNode target)
	{
		while (true)
		{
			// Call, Indexer, Access
			// TODO Contextual keywords
			// TODO Should not use identifier but instead a tightly-bound indexer, which is also used for type parameters
			// TODO An indexer or call expression could be chained with one another; this won't parse those
			var startIndex = index;
				
			// Access Expression
			if (Match(ref index, out var dot, TokenType.OpDot))
			{
				if (!Match(ref index, out var member, TokenType.Identifier))
				{
					// TODO Diagnostic
					index = startIndex;
					break;
				}
				
				var (source, range) = dot.SourceLocation;
				range = range.Join(member.SourceLocation.Range);
				target = new AccessExpressionNode(target, member, new(source, range));
				continue;
			}
			
			// Call Expression
			if (Match(ref index, TokenType.OpOpenParen))
			{
				if (IsNextNewline(index))
				{
					index = startIndex;
					break;
				}
				
				target = ParseCallExpression(ref index, target);
				continue;
			}
			
			// Indexer Expression
			if (Match(ref index, TokenType.OpOpenBracket))
			{
				if (IsNextNewline(index))
				{
					index = startIndex;
					break;
				}
				
				target = ParseIndexerExpression(ref index, target);
				continue;
			}
			
			break;
		}
		
		return target;
	}
	
	private CallExpressionNode ParseCallExpression(ref int index, IExpressionNode target)
	{
		// Caller already consumed open parenthesis
		var range = target.SourceLocation.Range;
		var arguments = new List<IExpressionNode>();
		
		Token closeParen;
		while (!Match(ref index, out closeParen, TokenType.OpCloseParen))
		{
			if (AtEnd(index))
			{
				// TODO Diagnostic
				range = range with { End = target.SourceLocation.Source.Length };
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
		
		return new(target, arguments, target.SourceLocation with { Range = range });
	}
	
	private IndexerExpressionNode ParseIndexerExpression(ref int index, IExpressionNode target)
	{
		// Caller already consumed open bracket
		var range = target.SourceLocation.Range;
		var arguments = new List<IExpressionNode>();
		
		Token closeBracket;
		while (!Match(ref index, out closeBracket, TokenType.OpCloseBracket))
		{
			if (AtEnd(index))
			{
				// TODO Diagnostic
				range = range with { End = target.SourceLocation.Source.Length };
				break;
			}
			
			if (arguments.Count > 0 && !Match(ref index, TokenType.OpComma))
			{
				// TODO Diagnostic: Missing comma
			}
			
			arguments.Add(ParseExpression(ref index));
		}
		
		if (closeBracket != default)
			range = range.Join(closeBracket.SourceLocation.Range);
		
		return new(target, arguments, target.SourceLocation with { Range = range });
	}
	
	private LiteralExpressionNode ParseLiteral(ref int index)
	{
		if (Match(ref index, out var literal, _literalTypes))
			return new(literal);
		
		// TODO Diagnostics
		// TEMP Should emit an erroneous node instead of throwing
		throw new InvalidOperationException();
	}
}