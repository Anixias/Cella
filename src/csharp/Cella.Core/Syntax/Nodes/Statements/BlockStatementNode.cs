using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class BlockStatementNode(SourceLocation sourceLocation, ImmutableArray<IStatementNode> statementNodes)
	: IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public ImmutableArray<IStatementNode> StatementNodes { get; } = statementNodes;
}