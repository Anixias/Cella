namespace Cella.Core.Syntax.Nodes;

public interface IExpressionNode : ISyntaxNode
{
	/// <summary>
	/// If <see langword="true"/>, the expression would not require parentheses with a unary operator.
	/// </summary>
	bool IsContained { get; }
}

[TreeVisitor<IExpressionNode>]
public partial interface IExpressionNodeVisitor;

[TreeVisitor<IExpressionNode>]
public partial interface IExpressionNodeVisitor<out T>;