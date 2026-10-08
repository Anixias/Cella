using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public enum FStrPart
{
	Piece,
	Spec,
	Value
}

public sealed class ResolvedFStrPartExpressionNode
(
	TypeSymbol type,
	IResolvedExpressionNode target,
	FStrPart part,
	IResolvedExpressionNode index,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public IResolvedExpressionNode Target { get; } = target;
	public FStrPart Part { get; } = part;
	public IResolvedExpressionNode Index { get; } = index;
	public IExpressionNode Syntax { get; } = syntax;
}