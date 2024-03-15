using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class AssignmentExpression : ExpressionNode
{
	public readonly ExpressionNode left;
	public readonly ExpressionNode right;
	
	public AssignmentExpression(ExpressionNode left, ExpressionNode right, TextRange range) : base(range)
	{
		this.left = left;
		this.right = right;
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