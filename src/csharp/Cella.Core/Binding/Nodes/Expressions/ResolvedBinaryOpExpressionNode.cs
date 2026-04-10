using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedBinaryOpExpressionNode
(
	IResolvedExpressionNode left,
	IResolvedExpressionNode right,
	OperationImpl? operation
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = operation?.Result ?? NativeSymbols.Invalid;
	public bool IsConstant { get; } = left.IsConstant && right.IsConstant;
	public IResolvedExpressionNode Left { get; } = left;
	public IResolvedExpressionNode Right { get; } = right;
	public OperationImpl? Operation { get; } = operation;
}