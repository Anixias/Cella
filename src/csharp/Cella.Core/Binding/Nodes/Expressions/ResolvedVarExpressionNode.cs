using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedVarExpressionNode(VariableSymbol symbol, TypeSymbol type, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public VariableSymbol Symbol { get; } = symbol;
	public TypeSymbol Type { get; } = type;
	public bool IsConstant => false; // TODO We should be able to detect if the variable is constant
	public IExpressionNode Syntax { get; } = syntax;
}