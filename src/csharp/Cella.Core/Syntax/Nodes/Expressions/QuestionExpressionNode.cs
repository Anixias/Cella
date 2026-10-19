using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class QuestionExpressionNode(IExpressionNode value, Token op, SourceLocation sourceLocation)
	: IExpressionNode
{
	public IExpressionNode Value { get; } = value;
	public Token Op { get; } = op;
	
	public SourceLocation SourceLocation { get; } = sourceLocation;
	
	public bool IsContained => true;
}