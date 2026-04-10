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

// TODO Is this needed?
public sealed class IdentityConversion(TypeSymbol symbol)
	: Conversion(symbol, symbol, ConversionKind.Identity, 0, true);

public sealed class IntegerConversion(IntegerType from, IntegerType to, ConversionKind kind, int cost)
	: Conversion(from, to, kind, cost, true)
{
	public bool FromSigned { get; } = from.IsSigned;
	public bool ToSigned { get; } = to.IsSigned;
}

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