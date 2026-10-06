using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedPropertyAssignmentExpressionNode
(
	FunctionInfo getter,
	FunctionInfo setter,
	IResolvedExpressionNode? receiver,
	Token op,
	IResolvedExpressionNode right,
	OperationImpl operation,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public FunctionInfo Getter { get; } = getter;
	public FunctionInfo Setter { get; } = setter;
	public IResolvedExpressionNode? Receiver { get; } = receiver;
	public Token Op { get; } = op;
	public IResolvedExpressionNode Right { get; } = right;
	public OperationImpl Operation { get; } = operation;
	public TypeSymbol Type { get; } = NativeSymbols.Void;
	public IExpressionNode Syntax { get; } = syntax;
}