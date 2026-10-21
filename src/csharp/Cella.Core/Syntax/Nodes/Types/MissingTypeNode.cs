using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class MissingTypeNode(SourceLocation sourceLocation) : ITypeNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
}