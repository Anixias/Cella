namespace Cella.Analysis.Syntax;

public sealed class TypeExpression : ExpressionNode
{
	public readonly SyntaxType type;

	public TypeExpression(SyntaxType type) : base(type.range)
	{
		this.type = type;
	}

	public override string ToString()
	{
		return type.ToString();
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