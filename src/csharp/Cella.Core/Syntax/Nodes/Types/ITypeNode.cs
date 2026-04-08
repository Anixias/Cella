namespace Cella.Core.Syntax.Nodes;

public interface ITypeNode : ISyntaxNode;

[TreeVisitor<ITypeNode>]
public partial interface ITypeNodeVisitor
{
}

[TreeVisitor<ITypeNode>]
public partial interface ITypeNodeVisitor<out T>
{
}