using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class BreakStatementNode(SourceLocation sourceLocation, IExpressionNode? expressionNode)
	: IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode? ExpressionNode { get; } = expressionNode;
}