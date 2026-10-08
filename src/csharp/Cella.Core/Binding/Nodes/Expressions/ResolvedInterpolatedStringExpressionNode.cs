using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedInterpolatedStringExpressionNode
(
	IEnumerable<IResolvedExpressionNode> values,
	InterpolatedStringExpressionNode syntax
) : IResolvedExpressionNode
{
	public ImmutableArray<IResolvedExpressionNode> Values { get; } = values.ToImmutableArray();
	public InterpolatedStringExpressionNode Interpolation { get; } = syntax;
	public TypeSymbol Type => InterpolatedStringType.Instance;
	public IExpressionNode Syntax => Interpolation;
}