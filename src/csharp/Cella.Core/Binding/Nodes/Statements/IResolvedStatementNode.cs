namespace Cella.Core.Binding.Nodes;

public interface IResolvedStatementNode : IResolvedNode
{
}

[TreeVisitor<IResolvedStatementNode>]
public partial interface IResolvedStatementNodeVisitor
{
}

[TreeVisitor<IResolvedStatementNode>]
public partial interface IResolvedStatementNodeVisitor<out T>
{
}