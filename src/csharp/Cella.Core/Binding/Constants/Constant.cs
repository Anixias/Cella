using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Constants;

public abstract class Constant(TypeSymbol type)
{
	public TypeSymbol Type { get; } = type;
}

public sealed class InvalidConstant : Constant
{
	public static InvalidConstant Instance { get; } = new();
	
	private InvalidConstant() : base(NativeSymbols.Invalid)
	{
	}
}

public sealed class IntegerConstant(TypeSymbol type, BigInteger value) : Constant(type)
{
	public BigInteger Value { get; } = value;
}

public sealed class FloatConstant(TypeSymbol type, double value) : Constant(type)
{
	public double Value { get; } = value;
}

public sealed class BoolConstant : Constant
{
	public static BoolConstant True { get; } = new(true);
	public static BoolConstant False { get; } = new(false);
	
	public bool Value { get; }
	
	private BoolConstant(bool value) : base(NativeSymbols.Bool)
	{
		Value = value;
	}
	
	public static BoolConstant From(bool value) => value ? True : False;
}

public sealed class NullConstant(TypeSymbol type) : Constant(type);

public sealed class StringConstant(TypeSymbol type, object value) : Constant(type)
{
	public object Value { get; } = value;
	
	public string Text => Value switch
	{
		StrValue text => Encoding.UTF8.GetString(text.Bytes),
		byte[] bytes => Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1),
		_ => (string)Value
	};
}

public sealed class RecordConstant(TypeSymbol type, IEnumerable<Constant> fields) : Constant(type)
{
	public ImmutableArray<Constant> Fields { get; } = fields.ToImmutableArray();
}

public sealed class ArrayConstant(ArrayType type, IEnumerable<Constant> elements) : Constant(type)
{
	public ArrayType ArrayType { get; } = type;
	public ImmutableArray<Constant> Elements { get; } = elements.ToImmutableArray();
}

public sealed class FunctionConstant(FunctionInfo function, TypeSymbol type) : Constant(type)
{
	public FunctionInfo Function { get; } = function;
}

public sealed class EnumConstant(TypeSymbol type, EnumCaseSymbol enumCase, IEnumerable<Constant> payload)
	: Constant(type)
{
	public EnumCaseSymbol Case { get; } = enumCase;
	public ImmutableArray<Constant> Payload { get; } = payload.ToImmutableArray();
}

public sealed class EnumTagConstant(TypeSymbol type, BigInteger tag) : Constant(type)
{
	public BigInteger Tag { get; } = tag;
}

public sealed class ZeroConstant(TypeSymbol type) : Constant(type);