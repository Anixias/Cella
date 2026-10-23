using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ForIndexNode(Token name) : ISyntaxNode
{
	public Token Name { get; } = name;
	public SourceLocation SourceLocation => Name.SourceLocation;
}