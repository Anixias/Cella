namespace Cella.Core.Binding.Nodes.Statements;

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