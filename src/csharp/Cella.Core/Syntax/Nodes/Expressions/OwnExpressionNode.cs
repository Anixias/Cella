using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class OwnExpressionNode(Token keyword, IExpressionNode value, SourceLocation sourceLocation)
	: IExpressionNode
{
	public Token Keyword { get; } = keyword;
	public IExpressionNode Value { get; } = value;
	
	public SourceLocation SourceLocation { get; } = sourceLocation;
	
	public bool IsContained => false;
}