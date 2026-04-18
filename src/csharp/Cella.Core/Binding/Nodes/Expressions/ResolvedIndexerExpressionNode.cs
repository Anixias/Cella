using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedIndexerExpressionNode(TypeSymbol type, IResolvedExpressionNode target,
	IResolvedExpressionNode index) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public IResolvedExpressionNode Target { get; } = target;
	public IResolvedExpressionNode Index { get; } = index;
	public bool IsConstant => Target.IsConstant && Index.IsConstant;
}