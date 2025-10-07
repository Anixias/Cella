using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class ExpressionParser(ImmutableArray<Token> tokens) : BaseParser<IExpressionNode>(tokens)
{
	private static readonly HashSet<TokenType> _literalTypes =
	[
		TokenType.IntegerLiteral
	];
	
	public override IExpressionNode? Parse(ref int index)
	{
		// @TEMP For now, assume the only valid expression is an integer literal
		if (Match(ref index, out var literal, _literalTypes))
			return new LiteralExpressionNode(literal);
		
		// @TODO Diagnostics
		return null;
	}
}