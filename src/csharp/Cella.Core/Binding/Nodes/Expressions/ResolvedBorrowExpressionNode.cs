using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedBorrowExpressionNode
(
	IResolvedExpressionNode place,
	BorrowType type,
	bool isImplicit,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public IResolvedExpressionNode Place { get; } = place;
	public TypeSymbol Type { get; } = type;
	public bool IsMutable { get; } = type.IsMutable;
	public bool IsImplicit { get; } = isImplicit;
	public IExpressionNode Syntax { get; } = syntax;
}