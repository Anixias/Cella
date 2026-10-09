using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class DropStatementNode(Token keyword, IExpressionNode target, SourceLocation sourceLocation)
	: IStatementNode
{
	public Token Keyword { get; } = keyword;
	public IExpressionNode Target { get; } = target;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}