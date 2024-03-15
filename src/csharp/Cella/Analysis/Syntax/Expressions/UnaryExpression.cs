using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class UnaryExpression : ExpressionNode
{
	public enum Operation
	{
		PreIncrement,
		PreDecrement,
		PostIncrement,
		PostDecrement,
		Identity,
		Negate,
		BitwiseNegate,
		LogicalNot,
		Await
	}
	
	public readonly ExpressionNode operand;
	public readonly Operation operation;
	public readonly Token op;
	public readonly bool isPrefix;

	public UnaryExpression(ExpressionNode operand, Operation operation, Token op, bool isPrefix, TextRange range) :
		base(range)
	{
		this.operand = operand;
		this.operation = operation;
		this.op = op;
		this.isPrefix = isPrefix;
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