using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class BreakExpressionNode(SourceLocation sourceLocation, IExpressionNode? label) : IExpressionNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public IExpressionNode? Label { get; } = label;
	public bool IsContained => false;
}