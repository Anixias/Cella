namespace Cella.Core.Syntax.Nodes;

public interface IDeclarationNode : ISyntaxNode;

[TreeVisitor<IDeclarationNode>]
public partial interface IDeclarationNodeVisitor
{
}

[TreeVisitor<IDeclarationNode>]
public partial interface IDeclarationNodeVisitor<out T>
{
}