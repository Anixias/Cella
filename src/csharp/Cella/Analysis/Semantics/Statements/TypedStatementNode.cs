using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics;

public abstract class TypedStatementNode
{
	public interface IVisitor<out T>
	{
		T Visit(TypedProgramStatement programStatement);
		T Visit(TypedEntryStatement entryStatement);
		T Visit(TypedBlockStatement blockStatement);
		T Visit(TypedReturnStatement returnStatement);
	}

	public interface IVisitor
	{
		void Visit(TypedProgramStatement programStatement);
		void Visit(TypedEntryStatement entryStatement);
		void Visit(TypedBlockStatement blockStatement);
		void Visit(TypedReturnStatement returnStatement);
	}
	
	public readonly TextRange range;

	protected TypedStatementNode(TextRange range)
	{
		this.range = range;
	}

	public abstract void Accept(IVisitor visitor);
	public abstract T Accept<T>(IVisitor<T> visitor);
}