using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedHeapExpressionNode(IResolvedExpressionNode? initializer, PointerType type,
	HeapExpressionNode syntax) : IResolvedExpressionNode
{
	public IResolvedExpressionNode? Initializer { get; } = initializer;
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
	public bool IsConstant => false;
}