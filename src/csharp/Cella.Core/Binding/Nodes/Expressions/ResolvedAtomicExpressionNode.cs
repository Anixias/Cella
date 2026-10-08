using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public enum AtomicAccess
{
	Load,
	Store,
	Modify,
	CompareSwap,
	Fence
}

public sealed class ResolvedAtomicExpressionNode
(
	AtomicAccess access,
	AtomicOrdering ordering,
	IResolvedExpressionNode? pointer,
	BinaryOperation? operation,
	IResolvedExpressionNode? expected,
	IResolvedExpressionNode? value,
	TypeSymbol type,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public AtomicAccess Access { get; } = access;
	public AtomicOrdering Ordering { get; } = ordering;
	public IResolvedExpressionNode? Pointer { get; } = pointer;
	public BinaryOperation? Operation { get; } = operation;
	public IResolvedExpressionNode? Expected { get; } = expected;
	public IResolvedExpressionNode? Value { get; } = value;
	public TypeSymbol Type { get; } = type;
	public IExpressionNode Syntax { get; } = syntax;
}