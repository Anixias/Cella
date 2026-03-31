using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedBinaryOpExpressionNode
(
	TypeSymbol type,
	IResolvedExpressionNode left,
	BinaryOperation op,
	IResolvedExpressionNode right
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public bool IsConstant { get; } = left.IsConstant && right.IsConstant;
	public IResolvedExpressionNode Left { get; } = left;
	public BinaryOperation Op { get; } = op;
	public IResolvedExpressionNode Right { get; } = right;
}