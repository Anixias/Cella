using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Statements;

public sealed class ResolvedVarStatementNode(LocalVariableSymbol symbol, IResolvedExpressionNode? initializer)
	: IResolvedStatementNode
{
	public LocalVariableSymbol Symbol { get; } = symbol;
	public IResolvedExpressionNode? Initializer { get; } = initializer;
}