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
	Bool,
	Str,
	CStr,
	Pointer,
	Array,
	Span
}

public static class NativeSymbols
{
	public static InvalidType Invalid => InvalidType.Instance;
	public static PrimitiveType Void { get; } = new("void", PrimitiveTypeKind.Void, StorageSize.Const(0));
	public static IntegerType Int8 { get; } = new("i8", PrimitiveTypeKind.Int8, StorageSize.Const(1), true);
	public static IntegerType Int16 { get; } = new("i16", PrimitiveTypeKind.Int16, StorageSize.Const(2), true);
	public static IntegerType Int32 { get; } = new("i32", PrimitiveTypeKind.Int32, StorageSize.Const(4), true);
	public static IntegerType Int64 { get; } = new("i64", PrimitiveTypeKind.Int64, StorageSize.Const(8), true);
	public static IntegerType Int128 { get; } = new("i128", PrimitiveTypeKind.Int128, StorageSize.Const(16), true);
	public static IntegerType IntSize { get; } = new("isize", PrimitiveTypeKind.IntSize, StorageSize.Ptr, true);
	public static IntegerType UInt8 { get; } = new("u8", PrimitiveTypeKind.UInt8, StorageSize.Const(1), false);
	public static IntegerType UInt16 { get; } = new("u16", PrimitiveTypeKind.UInt16, StorageSize.Const(2), false);
	public static IntegerType UInt32 { get; } = new("u32", PrimitiveTypeKind.UInt32, StorageSize.Const(4), false);
	public static IntegerType UInt64 { get; } = new("u64", PrimitiveTypeKind.UInt64, StorageSize.Const(8), false);
	public static IntegerType UInt128 { get; } = new("u128", PrimitiveTypeKind.UInt128, StorageSize.Const(16), false);
	public static IntegerType UIntSize { get; } = new("usize", PrimitiveTypeKind.UIntSize, StorageSize.Ptr, false);
	public static PrimitiveType Bool { get; } = new("bool", PrimitiveTypeKind.Bool, StorageSize.Const(1));
	public static PrimitiveType Str { get; } = new("str", PrimitiveTypeKind.Str, StorageSize.Ptr);
	public static PrimitiveType CStr { get; } = new("cstr", PrimitiveTypeKind.CStr, StorageSize.Ptr);
	
	private static readonly ImmutableDictionary<string, PrimitiveType> _primitiveTypes = new[]
	{
		Int8, Int16, Int32, Int64, Int128, IntSize,
		UInt8, UInt16, UInt32, UInt64, UInt128, UIntSize,
		Bool, Str, CStr
	}.ToImmutableDictionary(static s => s.Name);
	
	public static Symbol? Resolve(string name) => _primitiveTypes.GetValueOrDefault(name);
}

public readonly record struct StrValue(ulong Length, byte[] Bytes);

public interface ISize;

public static class StorageSize
{
	public static PointerSize Ptr => default;
	public static ConstSize Const(int bytes) => new(bytes);
	public static SumSize Sum(params IEnumerable<ISize> sizes) => new(sizes);
	public static ProductSize Product(ISize size, BigInteger count) => new(size, count);
	
	public static uint CountBits(ISize size, uint pointerSize)
	{
		var result = 0u;
		
		switch (size)
		{
			case ConstSize s:
				result += (uint)s.Value * 8;
				break;
			
			case PointerSize:
				result += pointerSize;
				break;
			
			case SumSize s:
				result += (uint)s.Sizes.Sum(s => CountBits(s, pointerSize));
				break;
			
			case ProductSize s:
				result += (uint)(s.Count * CountBits(s.Size, pointerSize));
				break;
		}
		
		return result;
	}
}

public readonly record struct ConstSize(int Value) : ISize;
public readonly record struct PointerSize : ISize;
public readonly record struct SumSize : ISize
{
	public ImmutableArray<ISize> Sizes { get; init; }
	
	public SumSize(IEnumerable<ISize> sizes)
	{
		Sizes = sizes.ToImmutableArray();
	}
}
public readonly record struct ProductSize(ISize Size, BigInteger Count) : ISize;