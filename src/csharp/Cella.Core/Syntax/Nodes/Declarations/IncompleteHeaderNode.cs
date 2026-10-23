using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class IncompleteHeaderNode(Token identifier, IEnumerable<TypeParameterNode> typeParameters)
{
	public Token Identifier { get; } = identifier;
	public ImmutableArray<TypeParameterNode> TypeParameters { get; } = typeParameters.ToImmutableArray();
}