using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public interface ISyntaxNode
{
	SourceLocation SourceLocation { get; }
}