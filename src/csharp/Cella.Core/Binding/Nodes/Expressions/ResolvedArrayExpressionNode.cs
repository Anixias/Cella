using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedArrayExpressionNode : IResolvedExpressionNode
{
	public ImmutableArray<IResolvedExpressionNode> Values { get; }
	public TypeSymbol Type { get; }
	public bool IsConstant { get; }
	
	public ResolvedArrayExpressionNode(TypeSymbol type, IEnumerable<IResolvedExpressionNode> values)
	{
		Values = values.ToImmutableArray();
		Type = type;
		IsConstant = Values.All(static v => v.IsConstant);
	}
}