using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedAssignmentExpressionNode
(
	TypeSymbol type,
	IResolvedExpressionNode left,
	Token op,
	IResolvedExpressionNode right
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public bool IsConstant { get; } = left.IsConstant && right.IsConstant;
	public IResolvedExpressionNode Left { get; } = left;
	public Token Op { get; } = op;
	public IResolvedExpressionNode Right { get; } = right;
}