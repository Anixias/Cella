namespace Cella.Core.Binding.Nodes;

public interface IResolvedNode
{
}

[TreeVisitor<IResolvedNode>]
public partial interface IResolvedNodeVisitor
{
}

[TreeVisitor<IResolvedNode>]
public partial interface IResolvedNodeVisitor<out T>
{
}