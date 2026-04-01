using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedFunctionCallExpression(FunctionSymbol function,
	IEnumerable<IResolvedExpressionNode> arguments) : IResolvedExpressionNode
{
	public FunctionSymbol Function { get; } = function;
	public ImmutableArray<IResolvedExpressionNode> Arguments { get; } = arguments.ToImmutableArray();
	public TypeSymbol Type { get; } = function.ReturnType ?? NativeSymbols.Void;
	public bool IsConstant => false; // TODO We should be able to detect if the function body is constant
}