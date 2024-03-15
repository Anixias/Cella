using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class FunctionCallExpression : ExpressionNode
{
	public readonly ExpressionNode caller;
	public readonly ImmutableArray<ExpressionNode> arguments;

	public FunctionCallExpression(ExpressionNode caller, IEnumerable<ExpressionNode> arguments, TextRange range)
		: base(range)
	{
		this.caller = caller;
		this.arguments = arguments.ToImmutableArray();
	}

	public override void Accept(IVisitor visitor)
	{
		visitor.Visit(this);
	}

	public override T Accept<T>(IVisitor<T> visitor)
	{
		return visitor.Visit(this);
	}
}