using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class ListExpression : ExpressionNode
{
	public readonly ImmutableArray<ExpressionNode> expressions;
	public readonly SyntaxType? type;

	public ListExpression(IEnumerable<ExpressionNode> expressions, SyntaxType? type, TextRange range) : base(range)
	{
		this.type = type;
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