namespace Cella.Core.Syntax.Nodes;

public interface IExpressionNode : ISyntaxNode;

[TreeVisitor<IExpressionNode>]
public partial interface IExpressionNodeVisitor;

[TreeVisitor<IExpressionNode>]
public partial interface IExpressionNodeVisitor<out T>;