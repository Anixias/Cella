using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class MatchExpressionNode
(
	Token keyword,
	IExpressionNode value,
	IEnumerable<MatchExpressionArmNode> arms,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public Token Keyword { get; } = keyword;
	public IExpressionNode Value { get; } = value;
	public ImmutableArray<MatchExpressionArmNode> Arms { get; } = arms.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}

public sealed class MatchExpressionArmNode(PatternNode? pattern, SourceLocation sourceLocation, IExpressionNode value)
{
	public PatternNode? Pattern { get; } = pattern;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode Value { get; } = value;
}