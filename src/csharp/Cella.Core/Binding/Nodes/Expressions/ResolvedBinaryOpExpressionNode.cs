using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedBinaryOpExpressionNode
(
	IResolvedExpressionNode left,
	IResolvedExpressionNode right,
	OperationImpl? operation,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = operation?.ReturnType ?? NativeSymbols.Invalid;
	public bool IsConstant { get; } = left.IsConstant && right.IsConstant;
	public IExpressionNode Syntax { get; } = syntax;
	public IResolvedExpressionNode Left { get; } = left;
	public IResolvedExpressionNode Right { get; } = right;
	public OperationImpl? Operation { get; } = operation;
}