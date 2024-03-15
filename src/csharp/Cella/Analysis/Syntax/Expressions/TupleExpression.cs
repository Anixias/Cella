using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class TupleExpression : ExpressionNode
{
	public readonly ImmutableArray<ExpressionNode> expressions;

	public TupleExpression(IEnumerable<ExpressionNode> expressions, TextRange range) : base(range)
	{
		this.expressions = expressions.ToImmutableArray();
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