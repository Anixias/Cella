using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

// @TODO Strong type for statement nodes
public sealed class BlockStatementNode(SourceLocation sourceLocation, ImmutableArray<IStatementNode> statementNodes)
	: IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public ImmutableArray<IStatementNode> StatementNodes { get; } = statementNodes;
}