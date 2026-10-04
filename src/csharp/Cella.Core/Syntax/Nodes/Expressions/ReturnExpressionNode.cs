using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ReturnExpressionNode(SourceLocation sourceLocation, IExpressionNode? value) : IExpressionNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode? Value { get; } = value;
	public bool IsContained => false;
}