using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedMatchStatementNode
(
	IResolvedExpressionNode value,
	IEnumerable<ResolvedMatchArm> arms,
	bool isMut,
	bool ownsValue,
	IStatementNode syntax
) : IResolvedStatementNode
{
	public IResolvedExpressionNode Value { get; } = value;
	public ImmutableArray<ResolvedMatchArm> Arms { get; } = arms.ToImmutableArray();
	public bool IsMut { get; } = isMut;
	public bool OwnsValue { get; } = ownsValue;
	public IStatementNode Syntax { get; } = syntax;
}

public sealed class ResolvedMatchArm
(
	IEnumerable<ResolvedPattern> patterns,
	IResolvedExpressionNode? guard,
	IResolvedStatementNode body
)
{
	public ImmutableArray<ResolvedPattern> Patterns { get; } = patterns.ToImmutableArray();
	public IResolvedExpressionNode? Guard { get; } = guard;
	public IResolvedStatementNode Body { get; } = body;
}