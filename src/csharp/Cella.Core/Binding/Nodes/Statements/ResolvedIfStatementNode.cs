using Cella.Core.Binding.Nodes.Expressions;

namespace Cella.Core.Binding.Nodes.Statements;

public sealed class ResolvedIfStatementNode(IResolvedExpressionNode condition, IResolvedStatementNode then,
	IResolvedStatementNode? @else) : IResolvedStatementNode
{
	public IResolvedExpressionNode Condition { get; } = condition;
	public IResolvedStatementNode Then { get; } = then;
	public IResolvedStatementNode? Else { get; } = @else;
}