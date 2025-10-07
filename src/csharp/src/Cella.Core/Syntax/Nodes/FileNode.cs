using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class FileNode(SourceLocation sourceLocation) : ISyntaxNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public required string ModuleName { get; init; }
	public required ImmutableArray<ISyntaxNode> Nodes { get; init; } // @TODO Change to declaration nodes?
}