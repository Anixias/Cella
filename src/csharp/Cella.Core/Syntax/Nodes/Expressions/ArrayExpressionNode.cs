using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ArrayExpressionNode(IEnumerable<IExpressionNode> values, SourceLocation sourceLocation)
	: IExpressionNode
{
	public ImmutableArray<IExpressionNode> Values { get; } = values.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
}