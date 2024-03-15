using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class CastExpression : ExpressionNode
{
	public enum Operation
	{
		Is,
		As
	}
	
	public readonly ExpressionNode operand;
	public readonly Operation operation;
	public readonly Token op;
	public readonly SyntaxType type;

	public CastExpression(ExpressionNode operand, Operation operation, Token op, SyntaxType type, TextRange range)
		: base(range)
	{
		this.operand = operand;
		this.operation = operation;
		this.op = op;
		this.type = type;
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