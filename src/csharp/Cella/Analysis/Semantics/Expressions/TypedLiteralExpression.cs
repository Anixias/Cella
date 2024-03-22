using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics;

public sealed class TypedLiteralExpression : TypedExpressionNode
{
	public readonly Token token;
	
	public TypedLiteralExpression(Token token, DataType? dataType) : base(dataType, token.Range)
	{
		this.token = token;
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