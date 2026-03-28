namespace Cella.Core.Symbols;

public static class NativeSymbols
{
	public static InvalidType Invalid { get; } = new();
	public static PrimitiveType Int32 { get; } = new("i32");
	public static PrimitiveType Int64 { get; } = new("i64");
	public static PrimitiveType Int128 { get; } = new("i128");
}