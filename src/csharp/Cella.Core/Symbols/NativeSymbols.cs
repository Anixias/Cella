namespace Cella.Core.Symbols;

public enum PrimitiveTypeKind
{
	Void,
	Int32,
	Int64,
	Int128
}

public static class NativeSymbols
{
	public static InvalidType Invalid { get; } = new();
	public static PrimitiveType Void { get; } = new("void", PrimitiveTypeKind.Void);
	public static PrimitiveType Int32 { get; } = new("i32", PrimitiveTypeKind.Int32);
	public static PrimitiveType Int64 { get; } = new("i64", PrimitiveTypeKind.Int64);
	public static PrimitiveType Int128 { get; } = new("i128", PrimitiveTypeKind.Int128);
}