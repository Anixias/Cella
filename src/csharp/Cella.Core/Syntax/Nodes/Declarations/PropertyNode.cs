using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class PropertyNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	Token? keyword,
	ITypeNode? type,
	IEnumerable<FunctionNode> accessors
) : IDeclarationNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public Token? Keyword { get; } = keyword;
	public ITypeNode? Type { get; } = type;
	public ImmutableArray<FunctionNode> Accessors { get; } = accessors.ToImmutableArray();
}