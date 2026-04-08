using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class FileNode(SourceLocation sourceLocation) : IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public required string FileName { get; init; }
	public required ModuleName ModuleName { get; init; }
	public required ImmutableArray<IDeclarationNode> Declarations { get; init; }
	public required ImmutableArray<ImportExpression> Imports { get; init; }
}