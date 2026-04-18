using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedAccessExpressionNode(IResolvedExpressionNode target, MemberSymbol member, TypeSymbol type)
	: IResolvedExpressionNode
{
	public IResolvedExpressionNode Target { get; } = target;
	public MemberSymbol Member { get; } = member;
	public TypeSymbol Type { get; } = type;
	public bool IsConstant => false; // TODO Member should know if it is a constant
}