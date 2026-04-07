using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class WhileStatementNode(SourceLocation sourceLocation, IExpressionNode condition, IStatementNode body,
	Token? label) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode Condition { get; } = condition;
	public IStatementNode Body { get; } = body;
	public Token? Label { get; } = label;
}