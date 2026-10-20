using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class CallExpressionNode
(
	IExpressionNode target,
	IEnumerable<IExpressionNode> arguments,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public IExpressionNode Target { get; } = target;
	public ImmutableArray<IExpressionNode> Arguments { get; } = arguments.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public required Token OpenParen { get; init; }
	public Token? CloseParen { get; init; }
	public bool IsContained => Target.IsContained;
}