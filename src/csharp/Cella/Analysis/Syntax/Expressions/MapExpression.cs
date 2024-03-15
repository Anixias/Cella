using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class MapExpression : ExpressionNode
{
	public readonly ImmutableArray<KeyValuePair<ExpressionNode, ExpressionNode>> keyValuePairs;
	public readonly SyntaxType.Tuple? type;

	public MapExpression(IEnumerable<KeyValuePair<ExpressionNode, ExpressionNode>> keyValuePairs,
		SyntaxType.Tuple? type, TextRange range) : base(range)
	{
		this.type = type;
		this.keyValuePairs = keyValuePairs.ToImmutableArray();
	}

	public override void Accept(IVisitor visitor)
	{
		visitor.Visit(this);
	}

	public override T Accept<T>(IVisitor<T> visitor)
	{
		return visitor.Visit(this);
	}
}