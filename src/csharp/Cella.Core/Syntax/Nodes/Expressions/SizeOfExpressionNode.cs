using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class SizeOfExpressionNode(Token token, IExpressionNode expression, SourceLocation sourceLocation)
	: IExpressionNode
{
	public Token Token { get; } = token;
	public IExpressionNode Expression { get; } = expression;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}