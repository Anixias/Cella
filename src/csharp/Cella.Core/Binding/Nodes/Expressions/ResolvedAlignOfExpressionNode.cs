using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedAlignOfExpressionNode(TypeSymbol target, IExpressionNode syntax) : IResolvedExpressionNode
{
	public TypeSymbol Target { get; } = target;
	public TypeSymbol Type { get; } = NativeSymbols.UIntSize;
	public IExpressionNode Syntax { get; } = syntax;
}