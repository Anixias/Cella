using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ForStatementNode
(
	SourceLocation sourceLocation,
	Token binding,
	Token? mode,
	IExpressionNode source,
	Token? rangeOperator,
	IExpressionNode? end,
	IStatementNode body,
	Token? label
) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public Token Binding { get; } = binding;
	public Token? Mode { get; } = mode;
	public IExpressionNode Source { get; } = source;
	public Token? RangeOperator { get; } = rangeOperator;
	public IExpressionNode? End { get; } = end;
	public IStatementNode Body { get; } = body;
	public Token? Label { get; } = label;
}