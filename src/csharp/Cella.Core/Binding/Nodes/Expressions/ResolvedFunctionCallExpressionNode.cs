using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedFunctionCallExpressionNode(FunctionInfo function,
	IEnumerable<IResolvedExpressionNode> arguments,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<IResolvedExpressionNode> Arguments { get; } = arguments.ToImmutableArray();
	public TypeSymbol Type { get; } = function.Signature.ReturnType;
	public bool IsConstant => false; // TODO We should be able to detect if the function body is constant
	public IExpressionNode Syntax { get; } = syntax;
}