using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class BorrowTypeNode(Token keyword, ITypeNode target, Token closeBracket) : ITypeNode
{
	public Token Keyword { get; } = keyword;
	public ITypeNode Target { get; } = target;
	public bool IsMutable => Keyword.Type == TokenType.KeywordMut;
	
	public SourceLocation SourceLocation { get; } = keyword.SourceLocation with
	{
		Range = keyword.SourceLocation.Range.Join(closeBracket.SourceLocation.Range)
	};
}