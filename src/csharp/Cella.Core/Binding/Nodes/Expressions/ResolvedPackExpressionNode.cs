using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedPackExpressionNode
(
	TypeSymbol type,
	TypeSymbol elementType,
	IEnumerable<IResolvedExpressionNode> values,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public TypeSymbol ElementType { get; } = elementType;
	public ImmutableArray<IResolvedExpressionNode> Values { get; } = values.ToImmutableArray();
	public IExpressionNode Syntax { get; } = syntax;
}