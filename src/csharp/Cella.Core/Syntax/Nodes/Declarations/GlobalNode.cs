using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class GlobalNode
(
	Token identifier,
	Token keyword,
	ITypeNode type,
	IExpressionNode? initializer
) : IDeclarationNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public SourceLocation Extent { get; init; } = identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public Token? Visibility { get; init; }
	public Token? WriteVisibility { get; init; }
	public Token Keyword { get; } = keyword;
	public ITypeNode Type { get; } = type;
	public IExpressionNode? Initializer { get; } = initializer;
	public bool IsMutable => Keyword.Type == TokenType.KeywordVar;
	public bool IsConstant => Keyword.Type == TokenType.KeywordConst;
	public bool IsExternal { get; init; }
	public Token? ExternalName { get; init; }
	public string? Origin { get; init; }
	public SourceLocation? OriginLocation { get; init; }
}