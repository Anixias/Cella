using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedIndirectCallExpressionNode
(
	IResolvedExpressionNode target,
	IEnumerable<IResolvedExpressionNode> arguments,
	FunctionType functionType,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public IResolvedExpressionNode Target { get; } = target;
	public ImmutableArray<IResolvedExpressionNode> Arguments { get; } = arguments.ToImmutableArray();
	public FunctionType FunctionType { get; } = functionType;
	public TypeSymbol Type { get; } = functionType.ReturnType;
	public IExpressionNode Syntax { get; } = syntax;
}