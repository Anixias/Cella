using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedChainedExpressionNode : IResolvedExpressionNode
{
	public TypeSymbol Type { get; }
	public ImmutableArray<IResolvedExpressionNode> Operands { get; }
	public ImmutableArray<ChainLink> Links { get; }
	public IExpressionNode Syntax { get; }
	
	public ResolvedChainedExpressionNode(TypeSymbol type, IEnumerable<IResolvedExpressionNode> operands,
		IEnumerable<ChainLink> links, IExpressionNode syntax)
	{
		Type = type;
		Syntax = syntax;
		Operands = operands.ToImmutableArray();
		Links = links.ToImmutableArray();
	}
}

public sealed record ChainLink(OperationImpl? Operation, FunctionInfo? Function)
{
	public Conversion? LeftConversion { get; init; }
	public Conversion? RightConversion { get; init; }
}