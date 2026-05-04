using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedUndefExpressionNode(TypeSymbol type) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public bool IsConstant => true;
}