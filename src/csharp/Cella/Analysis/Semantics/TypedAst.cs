using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics;

public sealed class TypedAst
{
	public TypedStatementNode Root { get; }
	public IBuffer Source { get; }
	
	public TypedAst(TypedStatementNode root, IBuffer source)
	{
		Root = root;
		Source = source;
	}
}