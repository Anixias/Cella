using System.Collections.Immutable;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedChainedExpressionNode : IResolvedExpressionNode
{
	public TypeSymbol Type { get; }
	public ImmutableArray<IResolvedExpressionNode> Operands { get; }
	public ImmutableArray<OperationImpl?> Ops { get; }
	public bool IsConstant { get; }
	public IExpressionNode Syntax { get; }
	
	public ResolvedChainedExpressionNode(TypeSymbol type, IEnumerable<IResolvedExpressionNode> operands,
		IEnumerable<OperationImpl?> ops, IExpressionNode syntax)
	{
		Type = type;
		Syntax = syntax;
		Operands = operands.ToImmutableArray();
		Ops = ops.ToImmutableArray();
		
		IsConstant = Operands.All(static o => o.IsConstant);
	}
}