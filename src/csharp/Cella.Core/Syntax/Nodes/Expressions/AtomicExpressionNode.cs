using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public enum AtomicOrdering
{
	SequentiallyConsistent,
	Relaxed,
	Acquire,
	Release,
	AcquireRelease
}

public sealed class AtomicExpressionNode
(
	Token keyword,
	AtomicOrdering ordering,
	SourceLocation? orderingLocation,
	IExpressionNode? place,
	Token? op,
	IExpressionNode? expected,
	IExpressionNode? value
) : IExpressionNode
{
	public Token Keyword { get; } = keyword;
	public AtomicOrdering Ordering { get; } = ordering;
	public SourceLocation? OrderingLocation { get; } = orderingLocation;
	public IExpressionNode? Place { get; } = place;
	public Token? Op { get; } = op;
	public IExpressionNode? Expected { get; } = expected;
	public IExpressionNode? Value { get; } = value;
	
	public SourceLocation SourceLocation { get; } = keyword.SourceLocation with
	{
		Range = keyword.SourceLocation.Range.Join((value ?? place)?.SourceLocation.Range ??
		                                          orderingLocation?.Range ?? keyword.SourceLocation.Range)
	};
	
	public bool IsContained => false;
	
	public AtomicExpressionNode Write(Token op, IExpressionNode value) =>
		new(Keyword, Ordering, OrderingLocation, Place, op, null, value);
	
	public AtomicExpressionNode CompareSwap(Token op, IExpressionNode expected, IExpressionNode value) =>
		new(Keyword, Ordering, OrderingLocation, Place, op, expected, value);
}