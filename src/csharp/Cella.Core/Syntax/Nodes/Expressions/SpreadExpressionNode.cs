using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class SpreadExpressionNode(Token ellipsis, IExpressionNode value) : IExpressionNode
{
	public Token Ellipsis { get; } = ellipsis;
	public IExpressionNode Value { get; } = value;
	
	public SourceLocation SourceLocation { get; } = ellipsis.SourceLocation with
	{
		Range = ellipsis.SourceLocation.Range.Join(value.SourceLocation.Range)
	};
	
	public bool IsContained => false;
}