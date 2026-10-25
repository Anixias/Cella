using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ImplNode
(
	ITypeNode target,
	Token keyword,
	IEnumerable<ITypeNode> traits,
	IEnumerable<IDeclarationNode> members
) : IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = target.SourceLocation;
	public ITypeNode Target { get; } = target;
	public Token Keyword { get; } = keyword;
	public ImmutableArray<ITypeNode> Traits { get; } = traits.ToImmutableArray();
	public ImmutableArray<ITypeNode> ConstantTraits { get; init; } = [];
	public ImmutableArray<IDeclarationNode> Members { get; } = members.ToImmutableArray();
	public ImmutableArray<TypeParameterNode> TypeParameters { get; init; } = [];
	public SourceLocation? Body { get; init; }
	public Token? LastHeaderToken { get; init; }
	public bool IsBlanket { get; init; }
}