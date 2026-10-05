using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedMatchStatementNode
(
	IResolvedExpressionNode value,
	IEnumerable<ResolvedMatchArm> arms,
	bool isMut,
	IStatementNode syntax
) : IResolvedStatementNode
{
	public IResolvedExpressionNode Value { get; } = value;
	public ImmutableArray<ResolvedMatchArm> Arms { get; } = arms.ToImmutableArray();
	public bool IsMut { get; } = isMut;
	public IStatementNode Syntax { get; } = syntax;
}

public sealed class ResolvedMatchArm(ResolvedPattern? pattern, IResolvedStatementNode body)
{
	public ResolvedPattern? Pattern { get; } = pattern;
	public IResolvedStatementNode Body { get; } = body;
}