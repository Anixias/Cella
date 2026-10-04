using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class SizeOfExpressionNode(Token token, ISyntaxNode target, SourceLocation sourceLocation)
	: IExpressionNode
{
	public Token Token { get; } = token;
	public ISyntaxNode Target { get; } = target;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}