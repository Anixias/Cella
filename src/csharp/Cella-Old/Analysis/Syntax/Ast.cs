using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public class Ast
{
	public StatementNode Root { get; }
	public IBuffer Source { get; }
	
	public Ast(StatementNode root, IBuffer source)
	{
		Root = root;
		Source = source;
	}
}