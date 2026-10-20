using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedTrapExpressionNode(IExpressionNode syntax) : IResolvedExpressionNode
{
	public TypeSymbol Type => NativeSymbols.Never;
	public IExpressionNode Syntax { get; } = syntax;
}