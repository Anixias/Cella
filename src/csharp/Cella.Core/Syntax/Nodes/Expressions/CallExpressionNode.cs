using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

// TODO Type parameters?
public sealed class CallExpressionNode(Token identifier, IEnumerable<IExpressionNode> arguments,
	SourceLocation sourceLocation) : IExpressionNode
{
	public Token Identifier { get; } = identifier;
	public ImmutableArray<IExpressionNode> Arguments { get; } = arguments.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
}