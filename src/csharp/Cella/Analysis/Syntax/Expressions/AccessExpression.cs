using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class AccessExpression : ExpressionNode
{
	public readonly ExpressionNode source;
	public readonly Token target;
	public readonly bool nullCheck;

	public AccessExpression(ExpressionNode source, Token target, bool nullCheck, TextRange range) : base(range)
	{
		this.source = source;
		this.target = target;
		this.nullCheck = nullCheck;
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