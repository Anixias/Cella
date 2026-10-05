using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedMutArgumentExpressionNode
(
	IResolvedExpressionNode place,
	PointerType type,
	MutArgumentExpressionNode syntax
) : IResolvedExpressionNode
{
	public IResolvedExpressionNode Place { get; } = place;
	public TypeSymbol Type { get; } = type;
	public MutArgumentExpressionNode Argument { get; } = syntax;
	public IExpressionNode Syntax { get; } = syntax;
}