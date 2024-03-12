using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public abstract class SyntaxType
{
	public sealed class Base : SyntaxType
	{
		public readonly Token identifier;

		public Base(Token identifier)
		{
			this.identifier = identifier;
		}

		public override string ToString() => identifier.Text;
	}

	public abstract class Wrapper : SyntaxType
	{
		public readonly SyntaxType baseType;

		protected Wrapper(SyntaxType baseType)
		{
			this.baseType = baseType;
		}
	}

	public sealed class Array : Wrapper
	{
		public readonly int dimensions;
		
		public Array(SyntaxType baseType, int dimensions) : base(baseType)
		{
			this.dimensions = dimensions;
		}

		public override string ToString()
		{
			return $"{baseType}[{new string(',', dimensions - 1)}]";
		}
	}
}