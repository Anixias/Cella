using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

// TODO Type parameters?
public sealed class CallExpressionNode(IExpressionNode target, IEnumerable<IExpressionNode> arguments,
	SourceLocation sourceLocation) : IExpressionNode
{
	public IExpressionNode Target { get; } = target;
	public ImmutableArray<IExpressionNode> Arguments { get; } = arguments.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => Target.IsContained;
}