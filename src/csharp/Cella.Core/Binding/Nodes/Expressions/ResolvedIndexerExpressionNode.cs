using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedIndexerExpressionNode(TypeSymbol type, IResolvedExpressionNode target,
	IResolvedExpressionNode index, IExpressionNode syntax) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public IResolvedExpressionNode Target { get; } = target;
	public IResolvedExpressionNode Index { get; } = index;
	public bool IsConstant => Target.IsConstant && Index.IsConstant;
	public IExpressionNode Syntax { get; } = syntax;
}