using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class InterpolatedStringExpressionNode
(
	IEnumerable<string> segments,
	IEnumerable<IExpressionNode> values,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public ImmutableArray<string> Segments { get; } = segments.ToImmutableArray();
	public ImmutableArray<IExpressionNode> Values { get; } = values.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}