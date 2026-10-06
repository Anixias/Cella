using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedAssignmentExpressionNode
(
	TypeSymbol type,
	IResolvedExpressionNode left,
	Token op,
	IResolvedExpressionNode right,
	OperationImpl? operation,
	IExpressionNode syntax,
	bool isOwnStore
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
	public IResolvedExpressionNode Left { get; } = left;
	public Token Op { get; } = op;
	public IResolvedExpressionNode Right { get; } = right;
	public OperationImpl? Operation { get; } = operation;
	public bool IsOwnStore { get; } = isOwnStore;
}