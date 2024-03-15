using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public abstract class SyntaxParameter
{
	public readonly Token identifier;

	protected SyntaxParameter(Token identifier)
	{
		this.identifier = identifier;
	}

	public sealed class Variable : SyntaxParameter
	{
		public readonly ImmutableArray<Token> modifiers;
		public readonly SyntaxType type;
		public readonly bool isVariadic;
		public readonly ExpressionNode? defaultValue;

		public Variable(Token identifier, SyntaxType type, IEnumerable<Token> modifiers, bool isVariadic,
			ExpressionNode? defaultValue = null) : base(identifier)
		{
			this.type = type;
			this.modifiers = modifiers.ToImmutableArray();
			this.isVariadic = isVariadic;
			this.defaultValue = defaultValue;
		}
	}

	public sealed class Self : SyntaxParameter
	{
		public readonly ImmutableArray<Token> modifiers;

		public Self(Token identifier, IEnumerable<Token> modifiers) : base(identifier)
		{
			this.modifiers = modifiers.ToImmutableArray();
		}
	}
}