using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedFunctionCallExpression(FunctionInfo function,
	IEnumerable<IResolvedExpressionNode> arguments) : IResolvedExpressionNode
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<IResolvedExpressionNode> Arguments { get; } = arguments.ToImmutableArray();
	public TypeSymbol Type { get; } = function.Signature.ReturnType;
	public bool IsConstant => false; // TODO We should be able to detect if the function body is constant
}