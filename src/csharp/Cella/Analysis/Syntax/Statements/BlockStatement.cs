using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class BlockStatement : StatementNode
{
	public readonly ImmutableArray<StatementNode> nodes;

	public BlockStatement(IEnumerable<StatementNode> nodes, TextRange range) : base(range)
	{
		this.nodes = nodes.ToImmutableArray();
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