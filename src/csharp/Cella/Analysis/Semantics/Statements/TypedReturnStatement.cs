using Cella.Analysis.Syntax;

namespace Cella.Analysis.Semantics;

public sealed class TypedReturnStatement : TypedStatementNode
{
	public TypedExpressionNode? Expression { get; set; }
	public readonly ReturnStatement sourceNode;

	public TypedReturnStatement(ReturnStatement sourceNode) : base(sourceNode.range)
	{
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
}