using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class MatchStatementNode
(
	SourceLocation sourceLocation,
	Token? mode,
	IExpressionNode value,
	IEnumerable<MatchArmNode> arms
) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public Token? Mode { get; } = mode;
	public IExpressionNode Value { get; } = value;
	public ImmutableArray<MatchArmNode> Arms { get; } = arms.ToImmutableArray();
}

public sealed class MatchArmNode(PatternNode? pattern, SourceLocation sourceLocation, IStatementNode body)
{
	public PatternNode? Pattern { get; } = pattern;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IStatementNode Body { get; } = body;
}