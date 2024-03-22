using System.Collections.Immutable;
using Cella.Analysis.Syntax;

namespace Cella.Analysis.Semantics;

public sealed class TypedBlockStatement : TypedStatementNode
{
	public readonly Scope scope;
	public readonly ImmutableArray<TypedStatementNode> statements;
	public readonly BlockStatement sourceNode;

	public TypedBlockStatement(Scope scope, IEnumerable<TypedStatementNode> statements, BlockStatement sourceNode)
		: base(sourceNode.range)
	{
		this.scope = scope;
		this.statements = statements.ToImmutableArray();
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