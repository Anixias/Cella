using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class ReturnStatement : StatementNode
{
	public readonly ExpressionNode? expression;

	public ReturnStatement(ExpressionNode? expression, TextRange range) : base(range)
	{
		this.expression = expression;
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