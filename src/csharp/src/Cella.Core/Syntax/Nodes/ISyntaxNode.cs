using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public interface ISyntaxNode
{
	SourceLocation SourceLocation { get; }
}

[TreeVisitor<ISyntaxNode>]
public partial interface ISyntaxNodeVisitor
{
}

[TreeVisitor<ISyntaxNode>]
public partial interface ISyntaxNodeVisitor<out T>
{
}