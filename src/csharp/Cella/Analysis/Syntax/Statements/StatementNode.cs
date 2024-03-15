using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public abstract class StatementNode
{
	public interface IVisitor<out T>
	{
		T Visit(ProgramStatement programStatement);
		T Visit(ImportStatement importStatement);
		T Visit(AggregateImportStatement aggregateImportStatement);
		T Visit(EntryStatement entryStatement);
		T Visit(BlockStatement blockStatement);
		T Visit(ReturnStatement returnStatement);
	}

	public interface IVisitor
	{
		void Visit(ProgramStatement programStatement);
		void Visit(ImportStatement importStatement);
		void Visit(AggregateImportStatement aggregateImportStatement);
		void Visit(EntryStatement entryStatement);
		void Visit(BlockStatement blockStatement);
		void Visit(ReturnStatement returnStatement);
	}
	
	public readonly TextRange range;

	protected StatementNode(TextRange range)
	{
		this.range = range;
	}

	public abstract void Accept(IVisitor visitor);
	public abstract T Accept<T>(IVisitor<T> visitor);
}