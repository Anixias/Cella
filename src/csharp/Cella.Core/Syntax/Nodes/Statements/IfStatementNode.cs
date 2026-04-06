using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class IfStatementNode(SourceLocation sourceLocation, IExpressionNode condition, IStatementNode then,
	IStatementNode? @else) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode Condition { get; } = condition;
	public IStatementNode Then { get; } = then;
	public IStatementNode? Else { get; } = @else;
}