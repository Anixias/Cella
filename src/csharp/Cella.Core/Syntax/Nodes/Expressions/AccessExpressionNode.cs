using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class AccessExpressionNode(IExpressionNode target, Token dot, Token member, SourceLocation sourceLocation)
	: IExpressionNode
{
	public IExpressionNode Target { get; } = target;
	public Token Dot { get; } = dot;
	public Token Member { get; } = member;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => Target.IsContained;
}