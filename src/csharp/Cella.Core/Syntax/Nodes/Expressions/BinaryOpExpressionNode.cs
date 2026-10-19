using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class BinaryOpExpressionNode
(
	IExpressionNode left,
	Token op,
	IExpressionNode right,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public IExpressionNode Left { get; } = left;
	public Token Op { get; } = op;
	public IExpressionNode Right { get; } = right;
	
	public SourceLocation SourceLocation { get; } = sourceLocation;
	
	public bool IsContained => false;
}