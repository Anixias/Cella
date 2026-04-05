using Cella.Core.Binding.Nodes.Expressions;

namespace Cella.Core.Binding.Nodes.Statements;

public sealed class ResolvedExpressionStatementNode(IResolvedExpressionNode expression) : IResolvedStatementNode
{
	public IResolvedExpressionNode Expression { get; } = expression;
}