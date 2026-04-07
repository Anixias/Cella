using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Statements;

public sealed class ResolvedWhileStatementNode(IResolvedExpressionNode condition, IResolvedStatementNode body,
	LabelSymbol? label)
	: IResolvedStatementNode
{
	public IResolvedExpressionNode Condition { get; } = condition;
	public IResolvedStatementNode Body { get; } = body;
	public LabelSymbol? Label { get; } = label;
}

public sealed class ResolvedDoWhileStatementNode(IResolvedStatementNode body, IResolvedExpressionNode condition,
	LabelSymbol? label)
	: IResolvedStatementNode
{
	public IResolvedStatementNode Body { get; } = body;
	public IResolvedExpressionNode Condition { get; } = condition;
	public LabelSymbol? Label { get; } = label;
}

public sealed class ResolvedRepeatStatementNode(IResolvedExpressionNode count, IResolvedStatementNode body,
	LabelSymbol? label)
	: IResolvedStatementNode
{
	public IResolvedExpressionNode Count { get; } = count;
	public IResolvedStatementNode Body { get; } = body;
	public LabelSymbol? Label { get; } = label;
}

public sealed class ResolvedLoopStatementNode(IResolvedStatementNode body, LabelSymbol? label)
	: IResolvedStatementNode
{
	public IResolvedStatementNode Body { get; } = body;
	public LabelSymbol? Label { get; } = label;
}