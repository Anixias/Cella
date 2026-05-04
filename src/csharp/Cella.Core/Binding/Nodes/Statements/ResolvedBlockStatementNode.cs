using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedBlockStatementNode(IEnumerable<IResolvedStatementNode> statements, IStatementNode syntax)
	: IResolvedStatementNode
{
	public ImmutableArray<IResolvedStatementNode> Statements { get; } = statements.ToImmutableArray();
	public IStatementNode Syntax { get; } = syntax;
}