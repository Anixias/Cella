using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class HeapExpressionNode(ISyntaxNode target, SourceLocation sourceLocation) : IExpressionNode
{
	public ISyntaxNode Target { get; } = target;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}