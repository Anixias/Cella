using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class UndefExpressionNode(ITypeNode? type, SourceLocation sourceLocation) : IExpressionNode
{
	public ITypeNode? Type { get; } = type;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}