using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class ConditionalExpression : ExpressionNode
{
	public readonly ExpressionNode condition;
	public readonly ExpressionNode trueExpression;
	public readonly ExpressionNode? falseExpression;

	public ConditionalExpression(ExpressionNode condition, ExpressionNode trueExpression,
		ExpressionNode? falseExpression, TextRange range) : base(range)
	{
		this.condition = condition;
		this.trueExpression = trueExpression;
		this.falseExpression = falseExpression;
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