using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public class Ast
{
	public SyntaxNode Root { get; }
	public IBuffer Source { get; }
	
	public Ast(SyntaxNode root, IBuffer source)
	{
		Root = root;
		Source = source;
	}
}