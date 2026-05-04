using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedIfStatementNode(IResolvedExpressionNode condition, IResolvedStatementNode then,
	IResolvedStatementNode? @else,
	IStatementNode syntax
) : IResolvedStatementNode
{
	public IResolvedExpressionNode Condition { get; } = condition;
	public IResolvedStatementNode Then { get; } = then;
	public IResolvedStatementNode? Else { get; } = @else;
	public IStatementNode Syntax { get; } = syntax;
}