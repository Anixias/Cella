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
	UInt8,
	UInt16,
	UInt32,
	UInt64,
	UInt128,
	Bool
}

public static class NativeSymbols
{
	public static InvalidType Invalid { get; } = new();
	public static PrimitiveType Void { get; } = new("void", PrimitiveTypeKind.Void, 0);
	public static PrimitiveType Int8 { get; } = new("i8", PrimitiveTypeKind.Int8, 1);
	public static PrimitiveType Int16 { get; } = new("i16", PrimitiveTypeKind.Int16, 2);
	public static PrimitiveType Int32 { get; } = new("i32", PrimitiveTypeKind.Int32, 4);
	public static PrimitiveType Int64 { get; } = new("i64", PrimitiveTypeKind.Int64, 8);
	public static PrimitiveType Int128 { get; } = new("i128", PrimitiveTypeKind.Int128, 16);
	public static PrimitiveType UInt8 { get; } = new("u8", PrimitiveTypeKind.UInt8, 1);
	public static PrimitiveType UInt16 { get; } = new("u16", PrimitiveTypeKind.UInt16, 2);
	public static PrimitiveType UInt32 { get; } = new("u32", PrimitiveTypeKind.UInt32, 4);
	public static PrimitiveType UInt64 { get; } = new("u64", PrimitiveTypeKind.UInt64, 8);
	public static PrimitiveType UInt128 { get; } = new("u128", PrimitiveTypeKind.UInt128, 12);
	public static PrimitiveType Bool { get; } = new("bool", PrimitiveTypeKind.Bool, 1);
	
	private static readonly ImmutableDictionary<string, PrimitiveType> _primitiveTypes = new[]
	{
		Int8, Int16, Int32, Int64, Int128,
		UInt8, UInt16, UInt32, UInt64, UInt128,
		Bool
	}.ToImmutableDictionary(static s => s.Name);
	
	public static Symbol? Resolve(string name) => _primitiveTypes.GetValueOrDefault(name);
}