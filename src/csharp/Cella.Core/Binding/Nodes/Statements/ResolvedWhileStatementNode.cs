using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedWhileStatementNode(IResolvedExpressionNode condition, IResolvedStatementNode body,
	LabelSymbol? label,
	IStatementNode syntax
) : IResolvedStatementNode
{
	public IResolvedExpressionNode Condition { get; } = condition;
	public IResolvedStatementNode Body { get; } = body;
	public LabelSymbol? Label { get; } = label;
	public IStatementNode Syntax { get; } = syntax;
}

public sealed class ResolvedDoWhileStatementNode(IResolvedStatementNode body, IResolvedExpressionNode condition,
	LabelSymbol? label,
	IStatementNode syntax
) : IResolvedStatementNode
{
	public IResolvedStatementNode Body { get; } = body;
	public IResolvedExpressionNode Condition { get; } = condition;
	public LabelSymbol? Label { get; } = label;
	public IStatementNode Syntax { get; } = syntax;
}

public sealed class ResolvedRepeatStatementNode(IResolvedExpressionNode count, IResolvedStatementNode body,
	LabelSymbol? label,
	IStatementNode syntax
) : IResolvedStatementNode
{
	public IResolvedExpressionNode Count { get; } = count;
	public IResolvedStatementNode Body { get; } = body;
	public LabelSymbol? Label { get; } = label;
	public IStatementNode Syntax { get; } = syntax;
}

public sealed class ResolvedLoopStatementNode(IResolvedStatementNode body, LabelSymbol? label, IStatementNode syntax)
	: IResolvedStatementNode
{
	public IResolvedStatementNode Body { get; } = body;
	public LabelSymbol? Label { get; } = label;
	public IStatementNode Syntax { get; } = syntax;
}