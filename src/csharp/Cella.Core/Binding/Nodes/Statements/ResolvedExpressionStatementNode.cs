namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedExpressionStatementNode(IResolvedExpressionNode expression) : IResolvedStatementNode
{
	public IResolvedExpressionNode Expression { get; } = expression;
}