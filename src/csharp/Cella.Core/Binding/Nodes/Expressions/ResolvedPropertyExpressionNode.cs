using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedPropertyExpressionNode
(
	PropertySymbol property,
	TypeSymbol owner,
	IResolvedExpressionNode? receiver,
	TypeSymbol type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public PropertySymbol Property { get; } = property;
	public TypeSymbol Owner { get; } = owner;
	public IResolvedExpressionNode? Receiver { get; } = receiver;
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}