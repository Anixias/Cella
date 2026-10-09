using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedClosureExpressionNode
(
	FunctionInfo function,
	IEnumerable<IResolvedExpressionNode> captures,
	FunctionType type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<IResolvedExpressionNode> Captures { get; } = captures.ToImmutableArray();
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}