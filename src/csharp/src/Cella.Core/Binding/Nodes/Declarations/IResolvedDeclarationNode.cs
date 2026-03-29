namespace Cella.Core.Binding.Nodes.Declarations;

public interface IResolvedDeclarationNode : IResolvedNode
{
}

[TreeVisitor<IResolvedDeclarationNode>]
public partial interface IResolvedDeclarationNodeVisitor
{
}

[TreeVisitor<IResolvedDeclarationNode>]
public partial interface IResolvedDeclarationNodeVisitor<out T>
{
}