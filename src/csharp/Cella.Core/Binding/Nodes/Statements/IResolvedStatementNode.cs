using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public interface IResolvedStatementNode : IResolvedNode
{
	IStatementNode Syntax { get; }
}

[TreeVisitor<IResolvedStatementNode>]
public partial interface IResolvedStatementNodeVisitor
{
}

[TreeVisitor<IResolvedStatementNode>]
public partial interface IResolvedStatementNodeVisitor<out T>
{
}