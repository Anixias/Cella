using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedTypeConstantExpressionNode
(
	TypeConstantUse use,
	TypeSymbol type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeConstantUse Use { get; } = use;
	public TypeSymbol Owner => Use.Owner;
	public GlobalSymbol Constant => Use.Constant;
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}