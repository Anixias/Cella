using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedVarStatementNode(LocalVariableSymbol symbol, IResolvedExpressionNode? initializer,
	IStatementNode syntax) : IResolvedStatementNode
{
	public LocalVariableSymbol Symbol { get; } = symbol;
	public IResolvedExpressionNode? Initializer { get; } = initializer;
	public IStatementNode Syntax { get; } = syntax;
}