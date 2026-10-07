using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedNewExpressionNode(TypeSymbol type, IExpressionNode syntax) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}