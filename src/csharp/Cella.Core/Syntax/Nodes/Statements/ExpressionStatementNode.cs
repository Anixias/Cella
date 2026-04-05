using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ExpressionStatementNode(IExpressionNode expressionNode)
	: IStatementNode
{
	public IExpressionNode ExpressionNode { get; } = expressionNode;
	public SourceLocation SourceLocation { get; } = expressionNode.SourceLocation;
}