using Cella.Core.Symbols;

namespace Cella.Core.Binding.Conversions;

public enum ConversionKind
{
	Identity,
	Implicit,
	Explicit
}

public abstract class Conversion(TypeSymbol from, TypeSymbol to, ConversionKind kind, int cost, bool isConstant)
{
	public TypeSymbol From { get; } = from;
	public TypeSymbol To { get; } = to;
	public ConversionKind Kind { get; } = kind;
	public int Cost { get; } = cost;
	public bool IsConstant { get; } = isConstant;
}

public sealed class NativeConversion(TypeSymbol from, TypeSymbol to, ConversionKind kind, int cost)
	: Conversion(from, to, kind, cost, true);

public sealed class FreeConversion(TypeSymbol from, TypeSymbol to, ConversionKind kind)
	: Conversion(from, to, kind, 1, true);

public sealed class EnumConversion(TypeSymbol from, TypeSymbol to)
	: Conversion(from, to, ConversionKind.Explicit, 1, true);

public sealed class NeverConversion(TypeSymbol to)
	: Conversion(NativeSymbols.Never, to, ConversionKind.Implicit, 0, false);

// TODO Is this needed?
public sealed class IdentityConversion(TypeSymbol symbol)
	: Conversion(symbol, symbol, ConversionKind.Identity, 0, true);

public sealed class IntegerConversion(IntegerType from, IntegerType to, ConversionKind kind, int cost)
	: Conversion(from, to, kind, cost, true)
{
	public bool FromSigned { get; } = from.IsSigned;
	public bool ToSigned { get; } = to.IsSigned;
}

public sealed class FloatConversion(PrimitiveType from, PrimitiveType to, ConversionKind kind, int cost)
	: Conversion(from, to, kind, cost, to is not IntegerType);

// TODO Detect constant functions
public sealed class FunctionConversion(FunctionInfo function, ConversionKind kind, int cost)
	: Conversion(function.Signature.ParameterTypes[0], function.Signature.ReturnType, kind, cost, false)
{
	public FunctionInfo Function { get; } = function;
}

[TreeVisitor<Conversion>]
public partial interface IConversionVisitor;

[TreeVisitor<Conversion>]
public partial interface IConversionVisitor<out T>;