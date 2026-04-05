using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

// TODO Type more complex than Token
public sealed class VarStatementNode(SourceLocation sourceLocation, Token identifier, Token? type,
	IExpressionNode? expressionNode) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public Token Identifier { get; } = identifier;
	public Token? Type { get; } = type;
	public IExpressionNode? ExpressionNode { get; } = expressionNode;
}