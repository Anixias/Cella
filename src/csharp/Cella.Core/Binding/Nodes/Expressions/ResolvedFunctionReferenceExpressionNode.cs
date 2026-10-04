using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedFunctionReferenceExpressionNode
(
	FunctionInfo function,
	FunctionType type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public FunctionInfo Function { get; } = function;
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}