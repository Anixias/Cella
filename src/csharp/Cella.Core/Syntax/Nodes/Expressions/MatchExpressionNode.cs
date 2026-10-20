using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class MatchExpressionNode
(
	Token keyword,
	Token? mode,
	IExpressionNode value,
	IEnumerable<MatchExpressionArmNode> arms,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public Token Keyword { get; } = keyword;
	public Token? Mode { get; } = mode;
	public IExpressionNode Value { get; } = value;
	public ImmutableArray<MatchExpressionArmNode> Arms { get; } = arms.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}

public sealed class MatchExpressionArmNode
(
	IEnumerable<PatternNode> patterns,
	IExpressionNode? guard,
	SourceLocation sourceLocation,
	IExpressionNode? value
)
{
	public ImmutableArray<PatternNode> Patterns { get; } = patterns.ToImmutableArray();
	public IExpressionNode? Guard { get; } = guard;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode? Value { get; } = value;
}