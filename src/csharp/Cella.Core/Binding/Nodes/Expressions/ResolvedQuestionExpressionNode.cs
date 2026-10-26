using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedQuestionExpressionNode
(
	FunctionInfo info,
	LocalVariableSymbol self,
	IResolvedExpressionNode operand,
	IResolvedExpressionNode? body,
	IResolvedStatementNode? statement,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public FunctionInfo Info { get; } = info;
	public FunctionSymbol Operator => Info.Symbol;
	public LocalVariableSymbol Self { get; } = self;
	public IResolvedExpressionNode Operand { get; } = operand;
	public IResolvedExpressionNode? Body { get; } = body;
	public IResolvedStatementNode? Statement { get; } = statement;
	public TypeSymbol Type { get; } = body?.Type ?? NativeSymbols.Void;
	public IExpressionNode Syntax { get; } = syntax;
}