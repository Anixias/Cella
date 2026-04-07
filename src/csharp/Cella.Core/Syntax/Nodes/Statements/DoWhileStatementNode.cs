using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class DoWhileStatementNode(SourceLocation sourceLocation, IStatementNode body, IExpressionNode condition,
	Token? label) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IStatementNode Body { get; } = body;
	public IExpressionNode Condition { get; } = condition;
	public Token? Label { get; } = label;
}