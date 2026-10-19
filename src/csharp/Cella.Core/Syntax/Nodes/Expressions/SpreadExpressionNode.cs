using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class SpreadExpressionNode(Token ellipsis, IExpressionNode value, SourceLocation sourceLocation)
	: IExpressionNode
{
	public IExpressionNode Value { get; } = value;
	
	public SourceLocation SourceLocation { get; } = sourceLocation;
	
	public bool IsContained => false;
}