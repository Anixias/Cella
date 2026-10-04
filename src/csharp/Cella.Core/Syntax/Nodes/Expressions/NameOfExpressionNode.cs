using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class NameOfExpressionNode(Token token, IExpressionNode name, SourceLocation sourceLocation)
	: IExpressionNode
{
	public Token Token { get; } = token;
	public IExpressionNode Name { get; } = name;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}