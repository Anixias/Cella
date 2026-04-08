using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class GenericTypeNode(SourceLocation sourceLocation, Token identifier,
	IEnumerable<ITypeNode> typeParameters) : ITypeNode
{
	public Token Identifier { get; } = identifier;
	public ImmutableArray<ITypeNode> TypeParameters { get; } = typeParameters.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
}