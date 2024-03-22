using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics;

public abstract class TypedStatementNode
{
	public interface IVisitor<out T>
	{
		T Visit(TypedProgramStatement typedProgramStatement);
		T Visit(TypedEntryStatement typedEntryStatement);
		T Visit(TypedBlockStatement typedBlockStatement);
		T Visit(TypedReturnStatement typedReturnStatement);
	}

	public interface IVisitor
	{
		void Visit(TypedProgramStatement typedProgramStatement);
		void Visit(TypedEntryStatement typedEntryStatement);
		void Visit(TypedBlockStatement typedBlockStatement);
		void Visit(TypedReturnStatement typedReturnStatement);
	}
	
	public readonly TextRange range;

	protected TypedStatementNode(TextRange range)
	{
		this.range = range;
	}

	public abstract void Accept(IVisitor visitor);
	public abstract T Accept<T>(IVisitor<T> visitor);
}