using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class BinaryOpExpressionNode(IExpressionNode left, Token op, IExpressionNode right) : IExpressionNode
{
	public IExpressionNode Left { get; } = left;
	public Token Op { get; } = op;
	public IExpressionNode Right { get; } = right;
	public SourceLocation SourceLocation { get; } = left.SourceLocation with
	{
		Range = left.SourceLocation.Range.Join(op.SourceLocation.Range.Join(right.SourceLocation.Range))
	};
	
	public bool IsContained => false;
}