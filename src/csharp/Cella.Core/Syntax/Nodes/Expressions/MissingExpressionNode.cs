using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class MissingExpressionNode(SourceLocation sourceLocation) : IExpressionNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}