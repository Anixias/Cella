using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class LoopStatementNode(SourceLocation sourceLocation, IStatementNode body,
	Token? label) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IStatementNode Body { get; } = body;
	public Token? Label { get; } = label;
}