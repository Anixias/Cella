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
}

public static class NativeSymbols
{
	public static InvalidType Invalid { get; } = new();
	public static PrimitiveType Void { get; } = new("void", PrimitiveTypeKind.Void, Size.Const(0));
	public static PrimitiveType Int8 { get; } = new("i8", PrimitiveTypeKind.Int8, Size.Const(1));
	public static PrimitiveType Int16 { get; } = new("i16", PrimitiveTypeKind.Int16, Size.Const(2));
	public static PrimitiveType Int32 { get; } = new("i32", PrimitiveTypeKind.Int32, Size.Const(4));
	public static PrimitiveType Int64 { get; } = new("i64", PrimitiveTypeKind.Int64, Size.Const(8));
	public static PrimitiveType Int128 { get; } = new("i128", PrimitiveTypeKind.Int128, Size.Const(16));
	public static PrimitiveType IntSize { get; } = new("isize", PrimitiveTypeKind.Int128, Size.Ptr);
	public static PrimitiveType UInt8 { get; } = new("u8", PrimitiveTypeKind.UInt8, Size.Const(1));
	public static PrimitiveType UInt16 { get; } = new("u16", PrimitiveTypeKind.UInt16, Size.Const(2));
	public static PrimitiveType UInt32 { get; } = new("u32", PrimitiveTypeKind.UInt32, Size.Const(4));
	public static PrimitiveType UInt64 { get; } = new("u64", PrimitiveTypeKind.UInt64, Size.Const(8));
	public static PrimitiveType UInt128 { get; } = new("u128", PrimitiveTypeKind.UInt128, Size.Const(16));
	public static PrimitiveType UIntSize { get; } = new("usize", PrimitiveTypeKind.Int128, Size.Ptr);
	public static PrimitiveType Bool { get; } = new("bool", PrimitiveTypeKind.Bool, Size.Const(1));
	public static PrimitiveType Str { get; } = new("str", PrimitiveTypeKind.Str, Size.Ptr);
	public static PrimitiveType CStr { get; } = new("cstr", PrimitiveTypeKind.CStr, Size.Ptr);
	
	private static readonly ImmutableDictionary<string, PrimitiveType> _primitiveTypes = new[]
	{
		Int8, Int16, Int32, Int64, Int128,
		UInt8, UInt16, UInt32, UInt64, UInt128,
		Bool, Str, CStr
	}.ToImmutableDictionary(static s => s.Name);
	
	public static Symbol? Resolve(string name) => _primitiveTypes.GetValueOrDefault(name);
}

public readonly record struct StrValue(ulong Length, byte[] Bytes);

public interface ISize;

public static class Size
{
	public static PointerSize Ptr => default;
	public static ConstSize Const(int bytes) => new(bytes);
}

public readonly record struct ConstSize(int Value) : ISize;
public readonly record struct PointerSize : ISize;