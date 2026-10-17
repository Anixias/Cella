using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class BrokenStatementNode(SourceLocation sourceLocation, Token? name, bool isMutable) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public Token? Name { get; } = name;
	public bool IsMutable { get; } = isMutable;
}