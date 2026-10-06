using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class BorrowExpressionNode(Token keyword, IExpressionNode value) : IExpressionNode
{
	public Token Keyword { get; } = keyword;
	public IExpressionNode Value { get; } = value;
	public bool IsMutable => Keyword.Type == TokenType.KeywordMut;
	
	public SourceLocation SourceLocation { get; } = keyword.SourceLocation with
	{
		Range = keyword.SourceLocation.Range.Join(value.SourceLocation.Range)
	};
	
	public bool IsContained => false;
}