using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class PanicExpressionNode(Token keyword, IExpressionNode? qualifier, IExpressionNode message)
	: IExpressionNode
{
	public Token Keyword { get; } = keyword;
	public IExpressionNode? Qualifier { get; } = qualifier;
	public IExpressionNode Message { get; } = message;
	
	public SourceLocation SourceLocation { get; } = (qualifier?.SourceLocation ?? keyword.SourceLocation) with
	{
		Range = (qualifier?.SourceLocation ?? keyword.SourceLocation).Range.Join(message.SourceLocation.Range)
	};
	
	public bool IsContained => false;
}