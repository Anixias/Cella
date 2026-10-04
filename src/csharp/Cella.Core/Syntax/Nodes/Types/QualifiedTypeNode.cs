using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class QualifiedTypeNode(SourceLocation sourceLocation, IEnumerable<Token> parts) : ITypeNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public ImmutableArray<Token> Parts { get; } = parts.ToImmutableArray();
}