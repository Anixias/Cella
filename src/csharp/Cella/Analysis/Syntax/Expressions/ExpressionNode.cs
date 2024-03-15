using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public abstract class ExpressionNode
{
	public interface IVisitor<out T>
	{
		T Visit(LambdaExpression lambdaExpression);
		T Visit(AssignmentExpression assignmentExpression);
		T Visit(ConditionalExpression conditionalExpression);
		T Visit(BinaryExpression binaryExpression);
		T Visit(UnaryExpression unaryExpression);
		T Visit(CastExpression castExpression);
		T Visit(AccessExpression accessExpression);
		T Visit(IndexExpression indexExpression);
		T Visit(FunctionCallExpression functionCallExpression);
		T Visit(TokenExpression tokenExpression);
		T Visit(TypeExpression typeExpression);
		T Visit(InterpolatedStringExpression interpolatedStringExpression);
		T Visit(TupleExpression tupleExpression);
		T Visit(ListExpression listExpression);
		T Visit(MapExpression mapExpression);
	}

	public interface IVisitor
	{
		void Visit(LambdaExpression lambdaExpression);
		void Visit(AssignmentExpression assignmentExpression);
		void Visit(ConditionalExpression conditionalExpression);
		void Visit(BinaryExpression binaryExpression);
		void Visit(UnaryExpression unaryExpression);
		void Visit(CastExpression castExpression);
		void Visit(AccessExpression accessExpression);
		void Visit(IndexExpression indexExpression);
		void Visit(FunctionCallExpression functionCallExpression);
		void Visit(TokenExpression tokenExpression);
		void Visit(TypeExpression typeExpression);
		void Visit(InterpolatedStringExpression interpolatedStringExpression);
		void Visit(TupleExpression tupleExpression);
		void Visit(ListExpression listExpression);
		void Visit(MapExpression mapExpression);
	}
	
	public readonly TextRange range;

	protected ExpressionNode(TextRange range)
	{
		this.range = range;
	}

	public abstract void Accept(IVisitor visitor);
	public abstract T Accept<T>(IVisitor<T> visitor);
}