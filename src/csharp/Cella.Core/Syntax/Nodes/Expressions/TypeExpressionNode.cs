using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class TypeExpressionNode(ITypeNode type) : IExpressionNode
{
	public ITypeNode Type { get; } = type;
	public SourceLocation SourceLocation => Type.SourceLocation;
	public bool IsContained => true;
}