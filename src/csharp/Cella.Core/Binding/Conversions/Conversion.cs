using System.Collections.Immutable;
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

public sealed class MatchConversion(TypeSymbol from, TypeSymbol to)
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

public sealed class DynConversion
(
	TypeSymbol from,
	TypeSymbol to,
	TypeSymbol objectType,
	ImmutableArray<FunctionInfo> members
) : Conversion(from, to, ConversionKind.Implicit, 1, false)
{
	public TypeSymbol ObjectType { get; } = objectType;
	public ImmutableArray<FunctionInfo> Members { get; } = members;
}

public sealed class DynUpcastConversion(TypeSymbol from, TypeSymbol to)
	: Conversion(from, to, ConversionKind.Implicit, 1, false);

public sealed class DynTestConversion(TypeSymbol from, TypeSymbol tested)
	: Conversion(from, NativeSymbols.Bool, ConversionKind.Explicit, 0, false)
{
	public TypeSymbol Tested { get; } = tested;
}

public sealed class DynCastConversion(TypeSymbol from, TypeSymbol to)
	: Conversion(from, to, ConversionKind.Explicit, 0, false);

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