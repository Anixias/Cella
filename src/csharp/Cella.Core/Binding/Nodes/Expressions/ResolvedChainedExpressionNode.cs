using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedChainedExpressionNode : IResolvedExpressionNode
{
	public TypeSymbol Type { get; }
	public ImmutableArray<IResolvedExpressionNode> Operands { get; }
	public ImmutableArray<Token> Ops { get; }
	public ImmutableArray<TypeSymbol> SubTypes { get; }
	public bool IsConstant { get; }
	
	public ResolvedChainedExpressionNode(TypeSymbol type, IEnumerable<IResolvedExpressionNode> operands,
		IEnumerable<Token> ops, IEnumerable<TypeSymbol> subTypes)
	{
		Type = type;
		Operands = operands.ToImmutableArray();
		Ops = ops.ToImmutableArray();
		SubTypes = subTypes.ToImmutableArray();
		
		IsConstant = Operands.All(static o => o.IsConstant);
	}
}