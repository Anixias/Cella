using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Operations;

public static class NativeOperations
{
	private static readonly ImmutableArray<TokenType> _numericBinOps =
	[
		TokenType.OpPlus,
		TokenType.OpPlusEqual,
		TokenType.OpMinus,
		TokenType.OpMinusEqual,
		TokenType.OpStar,
		TokenType.OpStarEqual,
		TokenType.OpSlash,
		TokenType.OpSlashEqual,
		TokenType.OpAmpersand,
		TokenType.OpBar,
		TokenType.OpHat,
	];
	
	private static readonly ImmutableArray<TokenType> _comparisonBinOps =
	[
		TokenType.OpGreater,
		TokenType.OpGreaterEqual,
		TokenType.OpLess,
		TokenType.OpLessEqual,
		TokenType.OpEqualEqual,
		TokenType.OpBangEqual,
	];
	
	private static readonly ImmutableArray<TokenType> _numericUnaryOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus,
		TokenType.OpBang,
	];
	
	private static readonly ImmutableArray<TokenType> _equalityBinOps =
	[
		TokenType.OpEqualEqual,
		TokenType.OpBangEqual,
	];
	
	public static ImmutableDictionary<NativeUnaryOperation, TypeSymbol> UnaryOperations { get; } =
		BuildUnaryOps().ToImmutableDictionary();
	
	public static ImmutableDictionary<NativeBinaryOperation, TypeSymbol> BinaryOperations { get; } =
		BuildBinaryOps().ToImmutableDictionary();
	
	public static TypeSymbol? Resolve(TypeSymbol left, TokenType op, TypeSymbol right) =>
		BinaryOperations.GetValueOrDefault(new NativeBinaryOperation(left, op, right));
	
	public static TypeSymbol? Resolve(TokenType op, TypeSymbol operand) =>
		UnaryOperations.GetValueOrDefault(new NativeUnaryOperation(op, operand));
	
	private static Dictionary<NativeBinaryOperation, TypeSymbol> BuildBinaryOps() =>
		new([
			..MakeSymmetricBinary(NativeSymbols.Int32, _numericBinOps),
			..MakeBinary(NativeSymbols.Int32, NativeSymbols.Bool, _comparisonBinOps),
			
			..MakeSymmetricBinary(NativeSymbols.Int64, _numericBinOps),
			..MakeBinary(NativeSymbols.Int64, NativeSymbols.Bool, _comparisonBinOps),
			
			..MakeSymmetricBinary(NativeSymbols.Int128, _numericBinOps),
			..MakeBinary(NativeSymbols.Int128, NativeSymbols.Bool, _comparisonBinOps),
			
			..MakeSymmetricBinary(NativeSymbols.Bool,
			[
				TokenType.OpAmpersand, TokenType.OpBar, TokenType.OpHat, .._equalityBinOps
			]),
		]);
	
	private static Dictionary<NativeUnaryOperation, TypeSymbol> BuildUnaryOps() =>
		new([
			..MakeSymmetricUnary(NativeSymbols.Int32, _numericUnaryOps),
			..MakeSymmetricUnary(NativeSymbols.Int64, _numericUnaryOps),
			..MakeSymmetricUnary(NativeSymbols.Int128, _numericUnaryOps),
		]);
	
	private static IEnumerable<KeyValuePair<NativeBinaryOperation, TypeSymbol>> MakeSymmetricBinary(TypeSymbol type,
		IEnumerable<TokenType> ops)
	{
		foreach (var op in ops)
			yield return MakeSymmetricBinary(type, op);
	}
	
	private static IEnumerable<KeyValuePair<NativeUnaryOperation, TypeSymbol>> MakeSymmetricUnary(TypeSymbol type,
		IEnumerable<TokenType> ops)
	{
		foreach (var op in ops)
			yield return MakeSymmetricUnary(type, op);
	}
	
	private static KeyValuePair<NativeBinaryOperation, TypeSymbol> MakeSymmetricBinary(TypeSymbol type,
		TokenType op) => new(new(type, op, type), type);
	
	private static KeyValuePair<NativeUnaryOperation, TypeSymbol> MakeSymmetricUnary(TypeSymbol type,
		TokenType op) => new(new(op, type), type);
	
	private static IEnumerable<KeyValuePair<NativeBinaryOperation, TypeSymbol>> MakeBinary(TypeSymbol input,
		TypeSymbol output, IEnumerable<TokenType> ops)
	{
		foreach (var op in ops)
			yield return MakeBinary(input, output, op);
	}
	
	private static IEnumerable<KeyValuePair<NativeUnaryOperation, TypeSymbol>> MakeUnary(TypeSymbol input,
		TypeSymbol output, IEnumerable<TokenType> ops)
	{
		foreach (var op in ops)
			yield return MakeUnary(input, output, op);
	}
	
	private static KeyValuePair<NativeBinaryOperation, TypeSymbol> MakeBinary(TypeSymbol type,
		TypeSymbol output, TokenType op) => new(new(type, op, type), output);
	
	private static KeyValuePair<NativeUnaryOperation, TypeSymbol> MakeUnary(TypeSymbol input,
		TypeSymbol output, TokenType op) => new(new(op, input), output);
}

public readonly record struct NativeUnaryOperation(TokenType Op, TypeSymbol Operand);
public readonly record struct NativeBinaryOperation(TypeSymbol Left, TokenType Op, TypeSymbol Right);