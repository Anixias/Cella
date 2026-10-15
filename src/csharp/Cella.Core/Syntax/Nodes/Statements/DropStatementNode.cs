using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class DropStatementNode(IExpressionNode target, SourceLocation sourceLocation)
	: IStatementNode
{
	public IExpressionNode Target { get; } = target;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}