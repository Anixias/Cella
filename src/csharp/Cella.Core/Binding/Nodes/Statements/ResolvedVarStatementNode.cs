using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedVarStatementNode(LocalVariableSymbol symbol, IResolvedExpressionNode? initializer)
	: IResolvedStatementNode
{
	public LocalVariableSymbol Symbol { get; } = symbol;
	public IResolvedExpressionNode? Initializer { get; } = initializer;
}