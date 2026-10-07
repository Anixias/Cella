using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class DynTypeNode(Token keyword, ITypeNode trait, Token closeBracket) : ITypeNode
{
	public Token Keyword { get; } = keyword;
	public ITypeNode Trait { get; } = trait;
	
	public SourceLocation SourceLocation { get; } = keyword.SourceLocation with
	{
		Range = keyword.SourceLocation.Range.Join(closeBracket.SourceLocation.Range)
	};
}