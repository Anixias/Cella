using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class InterpolatedStringExpressionNode
(
	IEnumerable<string> segments,
	IEnumerable<IExpressionNode> values,
	IEnumerable<string> specs,
	IEnumerable<SourceLocation> specLocations,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public ImmutableArray<string> Segments { get; } = segments.ToImmutableArray();
	public ImmutableArray<IExpressionNode> Values { get; } = values.ToImmutableArray();
	public ImmutableArray<string> Specs { get; } = specs.ToImmutableArray();
	public ImmutableArray<SourceLocation> SpecLocations { get; } = specLocations.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}