using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ReturnStatementNode(SourceLocation sourceLocation, IExpressionNode? expressionNode)
	: IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode? ExpressionNode { get; } = expressionNode;
}