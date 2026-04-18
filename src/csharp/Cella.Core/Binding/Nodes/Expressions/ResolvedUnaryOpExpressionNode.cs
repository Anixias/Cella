using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedUnaryOpExpressionNode
(
	IResolvedExpressionNode operand,
	OperationImpl? operation
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = operation?.Result ?? NativeSymbols.Invalid;
	public bool IsConstant { get; } = operand.IsConstant;
	public IResolvedExpressionNode Operand { get; } = operand;
	public OperationImpl? Operation { get; } = operation;
}