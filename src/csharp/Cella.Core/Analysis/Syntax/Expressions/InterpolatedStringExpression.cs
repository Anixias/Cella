using System.Collections.Immutable;
using Cella.Core.Analysis.Text;

namespace Cella.Core.Analysis.Syntax;

public sealed class InterpolatedStringExpression : ExpressionNode
{
	public readonly ImmutableArray<ExpressionNode> parts;
	
	public InterpolatedStringExpression(IEnumerable<ExpressionNode> parts, TextRange range) : base(range)
	{
		this.parts = parts.ToImmutableArray();
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