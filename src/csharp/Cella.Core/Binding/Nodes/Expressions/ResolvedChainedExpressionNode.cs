using System.Collections.Immutable;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedChainedExpressionNode : IResolvedExpressionNode
{
	public TypeSymbol Type { get; }
	public ImmutableArray<IResolvedExpressionNode> Operands { get; }
	public ImmutableArray<OperationImpl?> Ops { get; }
	public bool IsConstant { get; }
	
	public ResolvedChainedExpressionNode(TypeSymbol type, IEnumerable<IResolvedExpressionNode> operands,
		IEnumerable<OperationImpl?> ops)
	{
		Type = type;
		Operands = operands.ToImmutableArray();
		Ops = ops.ToImmutableArray();
		
		IsConstant = Operands.All(static o => o.IsConstant);
	}
}