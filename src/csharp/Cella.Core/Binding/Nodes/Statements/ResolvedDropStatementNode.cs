using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedDropStatementNode(IResolvedExpressionNode target, IStatementNode syntax)
	: IResolvedStatementNode
{
	public IResolvedExpressionNode Target { get; } = target;
	public IStatementNode Syntax { get; } = syntax;
}