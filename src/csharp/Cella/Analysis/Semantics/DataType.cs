using System.Collections.Immutable;
using Cella.Analysis.Semantics.Symbols;

namespace Cella.Analysis.Semantics;

public abstract class DataType
{
	public abstract bool Equals(DataType? type);

	public static bool Matches(DataType? left, DataType? right)
	{
		if (left is null)
			return right is null || right.Equals(left);
		
		return left.Equals(right);
	}

	public sealed class Tuple : DataType
	{
		public readonly ImmutableArray<DataType> types;

		public Tuple(IEnumerable<DataType> types)
		{
			this.types = types.ToImmutableArray();
		}

		public override string ToString()
		{
			return $"({string.Join(", ", types.Select(t => t.ToString()))})";
		}

		public override bool Equals(DataType? type)
		{
			if (type is not Tuple tupleType)
				return false;

			if (types.Length != tupleType.types.Length)
				return false;

			for (var i = 0; i < types.Length; i++)
			{
				if (!types[i].Equals(tupleType.types[i]))
					return false;
			}

			return true;
		}
	}
	
	public abstract class Wrapper : DataType
	{
		public readonly DataType baseType;

		protected Wrapper(DataType baseType)
		{
			this.baseType = baseType;
		}
	}
	
	// Todo: Handle generics
	
	public sealed class Array : Wrapper
	{
		public Array(DataType baseType) : base(baseType)
		{
		}

		public override string ToString()
		{
			return $"{baseType}[]";
		}

		public override bool Equals(DataType? type)
		{
			if (type is not Array arrayType)
				return false;

			if (!baseType.Equals(arrayType.baseType))
				return false;

			return true;
		}
	}

	public sealed class Base : DataType
	{
		public readonly DataTypeSymbol dataTypeSymbol;

		public Base(DataTypeSymbol dataTypeSymbol)
		{
			this.dataTypeSymbol = dataTypeSymbol;
		}

		public override string ToString()
		{
			return dataTypeSymbol.Name;
		}

		public override bool Equals(DataType? type)
		{
			if (type is not Base baseType)
				return false;

			if (dataTypeSymbol != baseType.dataTypeSymbol)
				return false;

			return true;
		}
	}
}