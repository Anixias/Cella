namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedReturnStatementNode(IResolvedExpressionNode? expression) : IResolvedStatementNode
{
	public IResolvedExpressionNode? Expression { get; } = expression;
}