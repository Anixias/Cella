using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedTraitConstantExpressionNode
(
	TypeSymbol owner,
	GlobalSymbol constant,
	TypeSymbol type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Owner { get; } = owner;
	public GlobalSymbol Constant { get; } = constant;
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}