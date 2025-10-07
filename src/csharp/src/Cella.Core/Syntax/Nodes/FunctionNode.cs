using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class FunctionNode(Token identifier, Token returnType, BlockStatementNode body) : ISyntaxNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public Token ReturnType { get; } = returnType; // @TODO More complex return type
	public BlockStatementNode Body { get; } = body;
}