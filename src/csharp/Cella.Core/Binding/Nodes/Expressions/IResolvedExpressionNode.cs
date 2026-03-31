using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public interface IResolvedExpressionNode : IResolvedNode
{
	TypeSymbol Type { get; }
	bool IsConstant { get; }
}

[TreeVisitor<IResolvedExpressionNode>]
public partial interface IResolvedExpressionNodeVisitor
{
}

[TreeVisitor<IResolvedExpressionNode>]
public partial interface IResolvedExpressionNodeVisitor<out T>
{
}