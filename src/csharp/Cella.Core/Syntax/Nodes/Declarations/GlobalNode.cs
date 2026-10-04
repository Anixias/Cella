using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class GlobalNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	Token keyword,
	ITypeNode type,
	IExpressionNode initializer
) : IDeclarationNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token Keyword { get; } = keyword;
	public ITypeNode Type { get; } = type;
	public IExpressionNode Initializer { get; } = initializer;
	public bool IsMutable => Keyword.Type == TokenType.KeywordVar;
}