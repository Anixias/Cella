using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedReturnStatementNode(IResolvedExpressionNode? expression, IStatementNode syntax)
	: IResolvedStatementNode
{
	public IResolvedExpressionNode? Expression { get; } = expression;
	public IStatementNode Syntax { get; } = syntax;
}