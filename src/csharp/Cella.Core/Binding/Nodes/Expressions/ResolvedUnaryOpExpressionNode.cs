using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedUnaryOpExpressionNode
(
	TypeSymbol type,
	Token op,
	IResolvedExpressionNode operand
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public bool IsConstant { get; } = operand.IsConstant;
	public Token Op { get; } = op;
	public IResolvedExpressionNode Operand { get; } = operand;
}