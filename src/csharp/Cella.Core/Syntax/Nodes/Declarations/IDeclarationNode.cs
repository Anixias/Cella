namespace Cella.Core.Syntax.Nodes.Declarations;

public interface IDeclarationNode : ISyntaxNode;

[TreeVisitor<IDeclarationNode>]
public partial interface IDeclarationNodeVisitor
{
}

[TreeVisitor<IDeclarationNode>]
public partial interface IDeclarationNodeVisitor<out T>
{
}