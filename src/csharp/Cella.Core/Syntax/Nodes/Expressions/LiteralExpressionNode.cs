using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class LiteralExpressionNode(Token token) : IExpressionNode
{
	public Token Token { get; } = token;
	public SourceLocation SourceLocation { get; } = token.SourceLocation;
	public bool IsContained => true;
}