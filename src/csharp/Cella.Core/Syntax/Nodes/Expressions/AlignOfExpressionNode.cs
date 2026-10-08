using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class AlignOfExpressionNode(Token token, IExpressionNode target, SourceLocation sourceLocation)
	: IExpressionNode
{
	public Token Token { get; } = token;
	public IExpressionNode Target { get; } = target;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}