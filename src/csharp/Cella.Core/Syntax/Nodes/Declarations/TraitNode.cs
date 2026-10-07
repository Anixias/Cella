using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class TraitNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	IEnumerable<IDeclarationNode> members
) : IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public ImmutableArray<IDeclarationNode> Members { get; } = members.ToImmutableArray();
}