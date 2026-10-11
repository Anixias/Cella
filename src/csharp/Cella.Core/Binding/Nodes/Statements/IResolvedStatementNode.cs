using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public interface IResolvedStatementNode : IResolvedNode
{
	IStatementNode Syntax { get; }
}

[TreeVisitor<IResolvedStatementNode>]
public partial interface IResolvedStatementNodeVisitor;

public sealed class ResolvedInvalidStatementNode(IStatementNode syntax) : IResolvedStatementNode
{
	public IStatementNode Syntax { get; } = syntax;
}