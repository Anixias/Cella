using System.Collections.Immutable;

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
	
	private static readonly ImmutableDictionary<string, PrimitiveType> _primitiveTypes =
		new[] { Int32, Int64, Int128 }.ToImmutableDictionary(static s => s.Name);
	
	public static Symbol? Resolve(string name) => _primitiveTypes.GetValueOrDefault(name);
}