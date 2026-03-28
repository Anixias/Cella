using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes.Declarations;

public sealed class FileNode(SourceLocation sourceLocation) : IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public required Token ModuleIdentifier { get; init; }
	public required ImmutableArray<IDeclarationNode> Declarations { get; init; }
}