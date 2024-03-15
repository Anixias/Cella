using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class AssignmentExpression : ExpressionNode
{
	public readonly ExpressionNode left;
	public readonly Token op;
	public readonly ExpressionNode right;
	
	public AssignmentExpression(ExpressionNode left, Token op, ExpressionNode right, TextRange range) : base(range)
	{
		this.left = left;
		this.op = op;
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