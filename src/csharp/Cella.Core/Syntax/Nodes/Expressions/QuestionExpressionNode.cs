using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class QuestionExpressionNode(IExpressionNode value, Token op) : IExpressionNode
{
	public IExpressionNode Value { get; } = value;
	public Token Op { get; } = op;
	
	public SourceLocation SourceLocation { get; } = value.SourceLocation with
	{
		Range = value.SourceLocation.Range.Join(op.SourceLocation.Range)
	};
	
	public bool IsContained => true;
}