using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class RepeatStatementNode(SourceLocation sourceLocation, IExpressionNode count, IStatementNode body,
	Token? label) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode Count { get; } = count;
	public IStatementNode Body { get; } = body;
	public Token? Label { get; } = label;
}