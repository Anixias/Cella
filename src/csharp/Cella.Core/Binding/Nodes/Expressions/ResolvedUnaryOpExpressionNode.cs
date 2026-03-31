using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedUnaryOpExpressionNode
(
	TypeSymbol type,
	UnaryOperation op,
	IResolvedExpressionNode operand
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public bool IsConstant { get; } = operand.IsConstant;
	public UnaryOperation Op { get; } = op;
	public IResolvedExpressionNode Operand { get; } = operand;
}