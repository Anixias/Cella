using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

// TODO Type more complex than Token
public sealed class VarStatementNode
(
	SourceLocation sourceLocation,
	Token identifier,
	ITypeNode? type,
	IExpressionNode? expressionNode,
	bool isMutable
) : IStatementNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public Token Identifier { get; } = identifier;
	public ITypeNode? Type { get; } = type;
	public IExpressionNode? ExpressionNode { get; } = expressionNode;
	public bool IsMutable { get; } = isMutable;
}