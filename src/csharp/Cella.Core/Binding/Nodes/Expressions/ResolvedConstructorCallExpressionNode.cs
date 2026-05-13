using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedConstructorCallExpressionNode(FunctionInfo function,
	IEnumerable<IResolvedExpressionNode> arguments,
	TypeSymbol type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<IResolvedExpressionNode> Arguments { get; } = arguments.ToImmutableArray();
	public TypeSymbol Type { get; } = type; // Constructors always return void, bypass
	public IExpressionNode Syntax { get; } = syntax;
	public bool IsConstant => false;
}