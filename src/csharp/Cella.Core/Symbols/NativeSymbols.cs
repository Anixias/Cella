using System.Collections.Immutable;
using System.Numerics;

namespace Cella.Core.Symbols;

public enum PrimitiveTypeKind
{
	Void,
	Int8,
	Int16,
	Int32,
	Int64,
	Int128,
	IntSize,
	UInt8,
	UInt16,
	UInt32,
	UInt64,
	UInt128,
	UIntSize,
	Char,
	Bool,
	Str,
	CStr,
	Pointer,
	Array,
	Buffer,
	Span,
	View,
}

public static class NativeSymbols
{
	public static InvalidType Invalid => InvalidType.Instance;
	public static UntypedIntegerType UntypedInteger => UntypedIntegerType.Instance;
	public static UntypedNullType UntypedNull => UntypedNullType.Instance;
	public static UntypedStringType UntypedString => UntypedStringType.Instance;
	public static PointerType VoidPtr => PointerType.VoidPtr;
	public static PrimitiveType Void { get; } = new("void", PrimitiveTypeKind.Void);
	public static IntegerType Int8 { get; } = new("i8", PrimitiveTypeKind.Int8, true);
	public static IntegerType Int16 { get; } = new("i16", PrimitiveTypeKind.Int16, true);
	public static IntegerType Int32 { get; } = new("i32", PrimitiveTypeKind.Int32, true);
	public static IntegerType Int64 { get; } = new("i64", PrimitiveTypeKind.Int64, true);
	public static IntegerType Int128 { get; } = new("i128", PrimitiveTypeKind.Int128, true);
	public static IntegerType IntSize { get; } = new("isize", PrimitiveTypeKind.IntSize, true);
	public static IntegerType UInt8 { get; } = new("u8", PrimitiveTypeKind.UInt8, false);
	public static IntegerType UInt16 { get; } = new("u16", PrimitiveTypeKind.UInt16, false);
	public static IntegerType UInt32 { get; } = new("u32", PrimitiveTypeKind.UInt32, false);
	public static IntegerType UInt64 { get; } = new("u64", PrimitiveTypeKind.UInt64, false);
	public static IntegerType UInt128 { get; } = new("u128", PrimitiveTypeKind.UInt128, false);
	public static IntegerType UIntSize { get; } = new("usize", PrimitiveTypeKind.UIntSize, false);
	public static IntegerType Char { get; } = new("char", PrimitiveTypeKind.Char, false);
	public static PrimitiveType Bool { get; } = new("bool", PrimitiveTypeKind.Bool);
	public static StringType Str { get; } = new("str", PrimitiveTypeKind.Str);
	public static StringType CStr { get; } = new("cstr", PrimitiveTypeKind.CStr);
	
	private static readonly ImmutableDictionary<string, TypeSymbol> _primitiveTypes = new TypeSymbol[]
	{
		Int8, Int16, Int32, Int64, Int128, IntSize,
		UInt8, UInt16, UInt32, UInt64, UInt128, UIntSize,
		Char, Bool, Str, CStr, VoidPtr
	}.ToImmutableDictionary(static s => s.Name);
	
	public static ImmutableArray<IntegerType> IntegerTypes { get; } =
	[
		Int8,
		UInt8,
		Int16,
		UInt16,
		Int32,
		UInt32,
		Int64,
		UInt64,
		Int128,
		UInt128,
		IntSize,
		UIntSize,
		Char
	];
	
	public static Symbol? Resolve(string name) => _primitiveTypes.GetValueOrDefault(name);
}

public readonly record struct StrValue(ulong Length, byte[] Bytes);

public interface ISize;

public static class StorageSize
{
	public static PointerSize Ptr => default;
	public static ConstSize Const(int bytes) => new(bytes);
	public static SumSize Sum(params IEnumerable<ISize> sizes) => new(sizes);
	public static MaxSize Max(params IEnumerable<ISize> sizes) => new(sizes);
	public static ProductSize Product(ISize size, BigInteger count) => new(size, count);
	
	public static uint CountBits(this ISize size, uint pointerSize) => size switch
	{
		ConstSize s => (uint)s.Value * 8,
		PointerSize => pointerSize,
		SumSize s => (uint)s.Sizes.Sum(s => s.CountBits(pointerSize)),
		MaxSize s => s.Sizes.Max(s => s.CountBits(pointerSize)),
		ProductSize s => (uint)(s.Count * s.Size.CountBits(pointerSize)),
		_ => 0u
	};
}

public readonly record struct ConstSize(int Value) : ISize;
public readonly record struct PointerSize : ISize;
public readonly record struct ProductSize(ISize Size, BigInteger Count) : ISize;

public readonly record struct SumSize : ISize
{
	public ImmutableArray<ISize> Sizes { get; init; }
	public SumSize(IEnumerable<ISize> sizes) => Sizes = sizes.ToImmutableArray();
}

public readonly record struct MaxSize : ISize
{
	public ImmutableArray<ISize> Sizes { get; init; }
	public MaxSize(IEnumerable<ISize> sizes) => Sizes = sizes.ToImmutableArray();
}