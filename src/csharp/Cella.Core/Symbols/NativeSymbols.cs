using System.Collections.Immutable;

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
	public static PrimitiveType Int8 { get; } = new("i8", PrimitiveTypeKind.Int8, StorageSize.Const(1));
	public static PrimitiveType Int16 { get; } = new("i16", PrimitiveTypeKind.Int16, StorageSize.Const(2));
	public static PrimitiveType Int32 { get; } = new("i32", PrimitiveTypeKind.Int32, StorageSize.Const(4));
	public static PrimitiveType Int64 { get; } = new("i64", PrimitiveTypeKind.Int64, StorageSize.Const(8));
	public static PrimitiveType Int128 { get; } = new("i128", PrimitiveTypeKind.Int128, StorageSize.Const(16));
	public static PrimitiveType IntSize { get; } = new("isize", PrimitiveTypeKind.IntSize, StorageSize.Ptr);
	public static PrimitiveType UInt8 { get; } = new("u8", PrimitiveTypeKind.UInt8, StorageSize.Const(1));
	public static PrimitiveType UInt16 { get; } = new("u16", PrimitiveTypeKind.UInt16, StorageSize.Const(2));
	public static PrimitiveType UInt32 { get; } = new("u32", PrimitiveTypeKind.UInt32, StorageSize.Const(4));
	public static PrimitiveType UInt64 { get; } = new("u64", PrimitiveTypeKind.UInt64, StorageSize.Const(8));
	public static PrimitiveType UInt128 { get; } = new("u128", PrimitiveTypeKind.UInt128, StorageSize.Const(16));
	public static PrimitiveType UIntSize { get; } = new("usize", PrimitiveTypeKind.UIntSize, StorageSize.Ptr);
	public static PrimitiveType Bool { get; } = new("bool", PrimitiveTypeKind.Bool, StorageSize.Const(1));
	public static PrimitiveType Str { get; } = new("str", PrimitiveTypeKind.Str, StorageSize.Ptr);
	public static PrimitiveType CStr { get; } = new("cstr", PrimitiveTypeKind.CStr, StorageSize.Ptr);
	
	private static readonly ImmutableDictionary<string, PrimitiveType> _primitiveTypes = new[]
	{
		Int8, Int16, Int32, Int64, Int128, IntSize,
		UInt8, UInt16, UInt32, UInt64, UInt128, UIntSize,
		Bool, Str, CStr
	}.ToImmutableDictionary(static s => s.Name);
	
	private static readonly Dictionary<TypeSymbol, SpanType> _spanTypes = [];
	
	public static SpanType GetOrCreateArray(TypeSymbol elementType)
	{
		if (_spanTypes.TryGetValue(elementType, out var existing))
			return existing;
		
		var spanType = new SpanType(elementType);
		_spanTypes[elementType] = spanType;
		return spanType;
	}
	
	public static Symbol? Resolve(string name) => _primitiveTypes.GetValueOrDefault(name);
}

public readonly record struct StrValue(ulong Length, byte[] Bytes);

public interface ISize;

public static class StorageSize
{
	public static PointerSize Ptr => default;
	public static ConstSize Const(int bytes) => new(bytes);
	public static SumSize Sum(params IEnumerable<ISize> sizes) => new(sizes);
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