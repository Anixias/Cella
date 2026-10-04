using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedAccessExpressionNode
(
	IResolvedExpressionNode target,
	MemberSymbol member,
	TypeSymbol type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public IResolvedExpressionNode Target { get; } = target;
	public MemberSymbol Member { get; } = member;
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}