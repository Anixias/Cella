using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics;

public abstract class TypedExpressionNode
{
	public DataType? DataType { get; }
	public readonly TextRange range;
	
	public interface IVisitor<out T>
	{
		T Visit(TypedLiteralExpression typedLiteralExpression);
	}

	public interface IVisitor
	{
		void Visit(TypedLiteralExpression typedLiteralExpression);
	}

	protected TypedExpressionNode(DataType? dataType, TextRange range)
	{
		DataType = dataType;
		this.range = range;
	}

	public abstract void Accept(IVisitor visitor);
	public abstract T Accept<T>(IVisitor<T> visitor);
}