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
	
	private static Dictionary<NativeBinaryOperation, TypeSymbol> BuildBinaryOps() => new()
	{
		MakeSymmetricBinary(NativeSymbols.Int32, BinaryOperation.Addition),
		MakeSymmetricBinary(NativeSymbols.Int32, BinaryOperation.Subtraction),
		MakeSymmetricBinary(NativeSymbols.Int32, BinaryOperation.Multiplication),
		MakeSymmetricBinary(NativeSymbols.Int32, BinaryOperation.Division),
		
		MakeSymmetricBinary(NativeSymbols.Int64, BinaryOperation.Addition),
		MakeSymmetricBinary(NativeSymbols.Int64, BinaryOperation.Subtraction),
		MakeSymmetricBinary(NativeSymbols.Int64, BinaryOperation.Multiplication),
		MakeSymmetricBinary(NativeSymbols.Int64, BinaryOperation.Division),
		
		MakeSymmetricBinary(NativeSymbols.Int128, BinaryOperation.Addition),
		MakeSymmetricBinary(NativeSymbols.Int128, BinaryOperation.Subtraction),
		MakeSymmetricBinary(NativeSymbols.Int128, BinaryOperation.Multiplication),
		MakeSymmetricBinary(NativeSymbols.Int128, BinaryOperation.Division),
	};
	
	private static Dictionary<NativeUnaryOperation, TypeSymbol> BuildUnaryOps() => new()
	{
		MakeSymmetricUnary(NativeSymbols.Int32, UnaryOperation.Identity),
		MakeSymmetricUnary(NativeSymbols.Int32, UnaryOperation.Negation),
		
		MakeSymmetricUnary(NativeSymbols.Int64, UnaryOperation.Identity),
		MakeSymmetricUnary(NativeSymbols.Int64, UnaryOperation.Negation),
		
		MakeSymmetricUnary(NativeSymbols.Int128, UnaryOperation.Identity),
		MakeSymmetricUnary(NativeSymbols.Int128, UnaryOperation.Negation),
	};
	
	private static KeyValuePair<NativeBinaryOperation, TypeSymbol> MakeSymmetricBinary(TypeSymbol type,
		BinaryOperation op) => new(new(type, op, type), type);
	
	private static KeyValuePair<NativeUnaryOperation, TypeSymbol> MakeSymmetricUnary(TypeSymbol type,
		UnaryOperation op) => new(new(op, type), type);
}

public readonly record struct NativeUnaryOperation(UnaryOperation Op, TypeSymbol Operand);
public readonly record struct NativeBinaryOperation(TypeSymbol Left, BinaryOperation Op, TypeSymbol Right);