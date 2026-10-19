using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class UnaryOpExpressionNode(Token op, IExpressionNode operand, SourceLocation sourceLocation)
	: IExpressionNode
{
	public Token Op { get; } = op;
	public IExpressionNode Operand { get; } = operand;
	
	public SourceLocation SourceLocation { get; } = sourceLocation;
	
	public bool IsContained => true;
}