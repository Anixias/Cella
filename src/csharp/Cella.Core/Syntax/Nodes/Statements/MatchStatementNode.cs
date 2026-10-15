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

public sealed class MatchArmNode
(
	IEnumerable<PatternNode> patterns,
	IExpressionNode? guard,
	SourceLocation sourceLocation,
	IStatementNode body
)
{
	public ImmutableArray<PatternNode> Patterns { get; } = patterns.ToImmutableArray();
	public IExpressionNode? Guard { get; } = guard;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IStatementNode Body { get; } = body;
}