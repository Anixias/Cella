using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class TokenExpression : ExpressionNode
{
	public readonly Token token;

	public TokenExpression(Token token) : base(token.Range)
	{
		this.token = token;
	}

	public override string ToString()
	{
		return token.Text;
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