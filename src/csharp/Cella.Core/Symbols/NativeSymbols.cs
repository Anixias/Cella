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
	Float32,
	Float64,
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
	public static UntypedFloatType UntypedFloat => UntypedFloatType.Instance;
	public static UntypedNullType UntypedNull => UntypedNullType.Instance;
	public static UntypedStringType UntypedString => UntypedStringType.Instance;
	public static NeverType Never => NeverType.Instance;
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
	public static FloatType Float32 { get; } = new("f32", PrimitiveTypeKind.Float32);
	public static FloatType Float64 { get; } = new("f64", PrimitiveTypeKind.Float64);
	public static IntegerType Char { get; } = new("char", PrimitiveTypeKind.Char, false);
	public static PrimitiveType Bool { get; } = new("bool", PrimitiveTypeKind.Bool);
	public static StringType Str { get; } = new("str", PrimitiveTypeKind.Str);
	public static StringType CStr { get; } = new("cstr", PrimitiveTypeKind.CStr);
	
	private static readonly ImmutableDictionary<string, TypeSymbol> _primitiveTypes = new TypeSymbol[]
	{
		Int8, Int16, Int32, Int64, Int128, IntSize,
		UInt8, UInt16, UInt32, UInt64, UInt128, UIntSize,
		Float32, Float64,
		Char, Bool, Str, CStr, VoidPtr
	}.ToImmutableDictionary(static s => s.Name);
	
	public static ImmutableArray<IntegerType> PureIntegerTypes { get; } =
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
		UIntSize
	];
	
	public static ImmutableArray<IntegerType> IntegerTypes { get; } =
	[
		..PureIntegerTypes,
		Char
	];
	
	public static ImmutableArray<FloatType> FloatTypes { get; } =
	[
		Float32,
		Float64
	];
	
	public static Symbol? Resolve(string name) => _primitiveTypes.GetValueOrDefault(name);
	public static IEnumerable<TypeSymbol> PrimitiveTypes => _primitiveTypes.Values;
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
	
	extension(ISize size)
	{
		public uint CountBits(uint pointerSize) => size switch
		{
			ConstSize s => (uint)s.Value * 8,
			PointerSize => pointerSize,
			SumSize s => CountSumBits(s.Sizes, pointerSize),
			MaxSize s => Align(s.Sizes.Max(s => s.CountBits(pointerSize)), CountMaxAlignmentBits(s.Sizes, pointerSize)),
			ProductSize s => (uint)(s.Count * s.Size.CountBits(pointerSize)),
			_ => 0u
		};
		
		public uint CountAlignmentBits(uint pointerSize) => size switch
		{
			ConstSize { Value: > 0 } s => (uint)s.Value * 8,
			PointerSize => pointerSize,
			SumSize s => CountMaxAlignmentBits(s.Sizes, pointerSize),
			MaxSize s => CountMaxAlignmentBits(s.Sizes, pointerSize),
			ProductSize s => s.Size.CountAlignmentBits(pointerSize),
			_ => 8u
		};
	}
	
	private static uint CountSumBits(ImmutableArray<ISize> sizes, uint pointerSize)
	{
		var offset = 0u;
		foreach (var size in sizes)
			offset = Align(offset, size.CountAlignmentBits(pointerSize)) + size.CountBits(pointerSize);
		
		return Align(offset, CountMaxAlignmentBits(sizes, pointerSize));
	}
	
	private static uint CountMaxAlignmentBits(ImmutableArray<ISize> sizes, uint pointerSize) =>
		sizes.Aggregate(8u, (alignment, size) => Math.Max(alignment, size.CountAlignmentBits(pointerSize)));
	
	private static uint Align(uint bits, uint alignment) => (bits + alignment - 1) / alignment * alignment;
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