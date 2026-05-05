using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class UnaryOpExpressionNode(Token op, IExpressionNode operand) : IExpressionNode
{
	public Token Op { get; } = op;
	public IExpressionNode Operand { get; } = operand;
	public SourceLocation SourceLocation { get; } = op.SourceLocation with
	{
		Range = op.SourceLocation.Range.Join(operand.SourceLocation.Range)
	};
	
	public bool IsContained => true;
}