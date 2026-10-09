using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedRangeForStatementNode
(
	LocalVariableSymbol counter,
	IResolvedExpressionNode start,
	IResolvedExpressionNode end,
	bool isInclusive,
	ResolvedLoopVariable? source,
	ResolvedLoopVariable? element,
	IResolvedStatementNode body,
	LabelSymbol? label,
	IStatementNode syntax
) : IResolvedStatementNode
{
	public LocalVariableSymbol Counter { get; } = counter;
	public IResolvedExpressionNode Start { get; } = start;
	public IResolvedExpressionNode End { get; } = end;
	public bool IsInclusive { get; } = isInclusive;
	public ResolvedLoopVariable? Source { get; } = source;
	public ResolvedLoopVariable? Element { get; } = element;
	public IResolvedStatementNode Body { get; } = body;
	public LabelSymbol? Label { get; } = label;
	public IStatementNode Syntax { get; } = syntax;
}

public sealed class ResolvedCursorForStatementNode
(
	ResolvedLoopVariable cursor,
	IResolvedExpressionNode step,
	ResolvedLoopVariable? element,
	IResolvedStatementNode body,
	LabelSymbol? label,
	IStatementNode syntax
) : IResolvedStatementNode
{
	public ResolvedLoopVariable Cursor { get; } = cursor;
	public IResolvedExpressionNode Step { get; } = step;
	public ResolvedLoopVariable? Element { get; } = element;
	public IResolvedStatementNode Body { get; } = body;
	public LabelSymbol? Label { get; } = label;
	public IStatementNode Syntax { get; } = syntax;
}

public sealed record ResolvedLoopVariable(LocalVariableSymbol Symbol, IResolvedExpressionNode Value);