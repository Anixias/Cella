using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class LambdaExpression : ExpressionNode
{
	public readonly ImmutableArray<SyntaxParameter> parameters;
	public readonly SyntaxType? returnType;
	public readonly StatementNode body;

	public LambdaExpression(IEnumerable<SyntaxParameter> parameters, SyntaxType? returnType, StatementNode body,
		TextRange range) : base(range)
	{
		this.parameters = parameters.ToImmutableArray();
		this.returnType = returnType;
		this.body = body;
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