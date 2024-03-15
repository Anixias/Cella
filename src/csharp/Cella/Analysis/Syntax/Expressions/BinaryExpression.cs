using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class BinaryExpression : ExpressionNode
{
	public enum Operation
	{
		NullCoalescence,
		Equals,
		NotEquals,
		Or,
		Xor,
		And,
		LessThan,
		GreaterThan,
		LessEqual,
		GreaterEqual,
		RotLeft,
		RotRight,
		ShiftLeft,
		ShiftRight,
		Add,
		Subtract,
		Multiply,
		Divide,
		DivisibleBy,
		Modulo,
		Power,
		RangeInclusive,
		RangeExclusive
	}
	
	public readonly ExpressionNode left;
	public readonly Operation operation;
	public readonly Token op;
	public readonly ExpressionNode right;

	public BinaryExpression(ExpressionNode left, Operation operation, Token op, ExpressionNode right, TextRange range)
		: base(range)
	{
		this.left = left;
		this.operation = operation;
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