using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedAccessExpressionNode(IResolvedExpressionNode target, MemberSymbol member)
	: IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = member.Type;
	public IResolvedExpressionNode Target { get; } = target;
	public MemberSymbol Member { get; } = member;
	public bool IsConstant => false; // TODO Member should know if it is a constant
}