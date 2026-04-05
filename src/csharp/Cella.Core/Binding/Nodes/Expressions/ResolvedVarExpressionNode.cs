using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedVarExpressionNode(VariableSymbol symbol, TypeSymbol type) : IResolvedExpressionNode
{
	public VariableSymbol Symbol { get; } = symbol;
	public TypeSymbol Type { get; } = type;
	public bool IsConstant => false; // TODO We should be able to detect if the variable is constant
}