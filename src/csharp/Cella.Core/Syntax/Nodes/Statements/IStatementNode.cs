namespace Cella.Core.Syntax.Nodes;

public interface IStatementNode : ISyntaxNode;

[TreeVisitor<IStatementNode>]
public partial interface IStatementNodeVisitor;

[TreeVisitor<IStatementNode>]
public partial interface IStatementNodeVisitor<out T>;