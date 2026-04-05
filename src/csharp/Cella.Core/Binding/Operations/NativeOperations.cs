using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Extensions;

namespace Cella.Core.Binding.Operations;

public static class NativeOperations
{
	public static ImmutableDictionary<NativeUnaryOperation, TypeSymbol> UnaryOperations { get; } =
		BuildUnaryOps().ToImmutableDictionary();
	
	public static ImmutableDictionary<NativeBinaryOperation, TypeSymbol> BinaryOperations { get; } =
		BuildBinaryOps().ToImmutableDictionary();
	
	public static TypeSymbol? Resolve(TypeSymbol left, BinaryOperation op, TypeSymbol right) =>
		BinaryOperations.GetValueOrDefault(new NativeBinaryOperation(left, op, right));
	
	public static TypeSymbol? Resolve(UnaryOperation op, TypeSymbol operand) =>
		UnaryOperations.GetValueOrDefault(new NativeUnaryOperation(op, operand));
	
	private static Dictionary<NativeBinaryOperation, TypeSymbol> BuildBinaryOps() =>
		new([
			..MakeSymmetricBinaryFull(NativeSymbols.Int32),
			..MakeSymmetricBinaryFull(NativeSymbols.Int64),
			..MakeSymmetricBinaryFull(NativeSymbols.Int128),
		]);
	
	private static Dictionary<NativeUnaryOperation, TypeSymbol> BuildUnaryOps() =>
		new([
			..MakeSymmetricUnaryFull(NativeSymbols.Int32),
			..MakeSymmetricUnaryFull(NativeSymbols.Int64),
			..MakeSymmetricUnaryFull(NativeSymbols.Int128),
		]);
	
	private static IEnumerable<KeyValuePair<NativeBinaryOperation, TypeSymbol>> MakeSymmetricBinaryFull(TypeSymbol type)
	{
		for (var op = (BinaryOperation)0; op < BinaryOperation.Count; op++)
			yield return MakeSymmetricBinary(type, op);
	}
	
	private static IEnumerable<KeyValuePair<NativeUnaryOperation, TypeSymbol>> MakeSymmetricUnaryFull(TypeSymbol type)
	{
		for (var op = (UnaryOperation)0; op < UnaryOperation.Count; op++)
			yield return MakeSymmetricUnary(type, op);
	}
	
	private static KeyValuePair<NativeBinaryOperation, TypeSymbol> MakeSymmetricBinary(TypeSymbol type,
		BinaryOperation op) => new(new(type, op, type), type);
	
	private static KeyValuePair<NativeUnaryOperation, TypeSymbol> MakeSymmetricUnary(TypeSymbol type,
		UnaryOperation op) => new(new(op, type), type);
}

public readonly record struct NativeUnaryOperation(UnaryOperation Op, TypeSymbol Operand);
public readonly record struct NativeBinaryOperation(TypeSymbol Left, BinaryOperation Op, TypeSymbol Right);