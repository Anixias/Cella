using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class IndexExpression : ExpressionNode
{
	public readonly ExpressionNode source;
	public readonly ExpressionNode index;
	public readonly bool nullCheck;

	public IndexExpression(ExpressionNode source, ExpressionNode index, bool nullCheck, TextRange range) : base(range)
	{
		this.source = source;
		this.index = index;
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