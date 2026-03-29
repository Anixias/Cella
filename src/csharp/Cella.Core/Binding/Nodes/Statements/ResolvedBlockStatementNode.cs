using System.Collections.Immutable;

namespace Cella.Core.Binding.Nodes.Statements;

public sealed class ResolvedBlockStatementNode(IEnumerable<IResolvedStatementNode> statements)
	: IResolvedStatementNode
{
	public ImmutableArray<IResolvedStatementNode> Statements { get; } = statements.ToImmutableArray();
}