using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedArrayExpressionNode : IResolvedExpressionNode
{
	public ImmutableArray<IResolvedExpressionNode> Values { get; }
	public TypeSymbol Type { get; }
	public bool IsConstant { get; }
	public IExpressionNode Syntax { get; }
	
	public ResolvedArrayExpressionNode(TypeSymbol type, IEnumerable<IResolvedExpressionNode> values,
		IExpressionNode syntax)
	{
		Values = values.ToImmutableArray();
		Type = type;
		Syntax = syntax;
		IsConstant = Values.All(static v => v.IsConstant);
	}
}