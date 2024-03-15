using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public abstract class SyntaxType
{
	public TextRange range;

	protected SyntaxType(TextRange range)
	{
		this.range = range;
	}

	public override string ToString()
	{
		return "???";
	}

	public sealed class Base : SyntaxType
	{
		public readonly Token identifier;

		public Base(Token identifier) : base(identifier.Range)
		{
			this.identifier = identifier;
		}

		public override string ToString() => identifier.Text;
	}

	public abstract class Wrapper : SyntaxType
	{
		public readonly SyntaxType baseType;

		protected Wrapper(SyntaxType baseType, TextRange range) : base(range)
		{
			this.baseType = baseType;
		}
	}

	public sealed class Array : Wrapper
	{
		public readonly int dimensions;
		
		public Array(SyntaxType baseType, int dimensions, TextRange range) : base(baseType, range)
		{
			this.dimensions = dimensions;
		}

		public override string ToString()
		{
			return $"{baseType}[{new string(',', dimensions - 1)}]";
		}
	}

	public sealed class Tuple : SyntaxType
	{
		public readonly ImmutableArray<SyntaxType> types;

		public Tuple(IEnumerable<SyntaxType> types, TextRange range) : base(range)
		{
			this.types = types.ToImmutableArray();
		}

		public override string ToString()
		{
			return $"({string.Join(',', types.Select(t => t.ToString()))})";
		}
	}
}