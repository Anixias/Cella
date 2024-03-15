using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class EntryStatement : StatementNode
{
	public readonly Token name;
	public readonly ImmutableArray<SyntaxParameter> parameters;
	public readonly SyntaxType? returnType;
	public readonly ImmutableArray<Token> effects;
	public readonly BlockStatement body;

	public EntryStatement(Token name, IEnumerable<SyntaxParameter> parameters, SyntaxType? returnType,
		IEnumerable<Token> effects, BlockStatement body, TextRange range) : base(range)
	{
		this.name = name;
		this.parameters = parameters.ToImmutableArray();
		this.returnType = returnType;
		this.body = body;
		this.effects = effects.ToImmutableArray();
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