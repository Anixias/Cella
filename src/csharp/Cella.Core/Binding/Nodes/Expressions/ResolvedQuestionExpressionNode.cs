using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedQuestionExpressionNode
(
	LocalVariableSymbol self,
	IResolvedExpressionNode operand,
	IResolvedExpressionNode body,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public LocalVariableSymbol Self { get; } = self;
	public IResolvedExpressionNode Operand { get; } = operand;
	public IResolvedExpressionNode Body { get; } = body;
	public TypeSymbol Type { get; } = body.Type;
	public IExpressionNode Syntax { get; } = syntax;
}