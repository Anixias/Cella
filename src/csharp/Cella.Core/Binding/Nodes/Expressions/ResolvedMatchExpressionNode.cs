using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedMatchExpressionNode
(
	IResolvedExpressionNode value,
	IEnumerable<ResolvedMatchExpressionArm> arms,
	TypeSymbol type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public IResolvedExpressionNode Value { get; } = value;
	public ImmutableArray<ResolvedMatchExpressionArm> Arms { get; } = arms.ToImmutableArray();
	public IExpressionNode Syntax { get; } = syntax;
}

public sealed class ResolvedMatchExpressionArm(ResolvedPattern? pattern, IResolvedExpressionNode value)
{
	public ResolvedPattern? Pattern { get; } = pattern;
	public IResolvedExpressionNode Value { get; } = value;
}