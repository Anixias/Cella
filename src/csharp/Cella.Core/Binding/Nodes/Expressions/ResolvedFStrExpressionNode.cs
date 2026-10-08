using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedFStrExpressionNode
(
	FStrType type,
	IEnumerable<string> texts,
	IEnumerable<IResolvedExpressionNode> values,
	IResolvedExpressionNode? text,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public ImmutableArray<string> Texts { get; } = texts.ToImmutableArray();
	public ImmutableArray<IResolvedExpressionNode> Values { get; } = values.ToImmutableArray();
	public IResolvedExpressionNode? Text { get; } = text;
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}