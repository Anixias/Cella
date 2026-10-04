using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedEnumCaseExpressionNode
(
	EnumSymbol type,
	EnumCaseSymbol enumCase,
	IEnumerable<IResolvedExpressionNode> payload,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public EnumCaseSymbol Case { get; } = enumCase;
	public ImmutableArray<IResolvedExpressionNode> Payload { get; } = payload.ToImmutableArray();
	public IExpressionNode Syntax { get; } = syntax;
}