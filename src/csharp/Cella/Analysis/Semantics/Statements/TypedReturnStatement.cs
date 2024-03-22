using Cella.Analysis.Syntax;

namespace Cella.Analysis.Semantics;

public sealed class TypedReturnStatement : TypedStatementNode
{
	public readonly TypedExpressionNode? expression;
	public readonly ReturnStatement sourceNode;

	public TypedReturnStatement(ReturnStatement sourceNode) : this(sourceNode, null)
	{
	}

	private TypedReturnStatement(ReturnStatement sourceNode, TypedExpressionNode? expression) : base(sourceNode.range)
	{
		this.expression = expression;
		this.sourceNode = sourceNode;
	}

	public override void Accept(IVisitor visitor)
	{
		visitor.Visit(this);
	}

	public override T Accept<T>(IVisitor<T> visitor)
	{
		return visitor.Visit(this);
	}

	public TypedReturnStatement Resolve(TypedExpressionNode? expression)
	{
		return new TypedReturnStatement(sourceNode, expression);
	}
}