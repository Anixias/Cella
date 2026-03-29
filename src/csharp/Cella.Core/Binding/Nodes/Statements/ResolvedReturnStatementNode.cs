using Cella.Core.Binding.Nodes.Expressions;

namespace Cella.Core.Binding.Nodes.Statements;

public sealed class ResolvedReturnStatementNode(IResolvedExpressionNode? expression)
	: IResolvedStatementNode
{
	public IResolvedExpressionNode? Expression { get; } = expression;
}