using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class IdentifierTypeNode(Token token) : ITypeNode
{
	public Token Token { get; } = token;
	public SourceLocation SourceLocation { get; } = token.SourceLocation;
}