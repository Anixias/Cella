using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Operations;

public readonly record struct BinaryResolution
(
	OperationImpl? Operation,
	Conversion? LeftConversion,
	Conversion? RightConversion
);

public readonly record struct UnaryResolution
(
	OperationImpl? Operation,
	Conversion? OperandConversion
);

public sealed class OperatorRegistry(ConversionTable conversionTable)
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
		TokenType.OpPercent,
		TokenType.OpPercentEqual,
		TokenType.OpAmpersand,
		TokenType.OpAmpersandEqual,
		TokenType.OpBar,
		TokenType.OpBarEqual,
		TokenType.OpHat,
		TokenType.OpHatEqual,
	];
	
	private static readonly ImmutableArray<TokenType> _equalityBinOps =
	[
		TokenType.OpEqualEqual,
		TokenType.OpBangEqual,
	];
	
	private static readonly ImmutableArray<TokenType> _comparisonBinOps =
	[
		TokenType.OpGreater,
		TokenType.OpGreaterEqual,
		TokenType.OpLess,
		TokenType.OpLessEqual,
		TokenType.OpEqualEqual,
		TokenType.OpBangEqual,
		.._equalityBinOps
	];
	
	private static readonly ImmutableArray<TokenType> _numericUnaryOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus,
		TokenType.OpBang,
	];
	
	private static readonly ImmutableArray<TokenType> _boolBinOps =
	[
		TokenType.OpAmpersandEqual,
		TokenType.OpAmpersand,
		TokenType.OpBarEqual,
		TokenType.OpBar,
		TokenType.OpHatEqual,
		TokenType.OpHat,
		.._equalityBinOps
	];
	
	private static readonly ImmutableArray<TypeSymbol> _numericTypes =
	[
		NativeSymbols.Int8, NativeSymbols.UInt8,
		NativeSymbols.Int16, NativeSymbols.UInt16,
		NativeSymbols.Int32, NativeSymbols.UInt32,
		NativeSymbols.Int64, NativeSymbols.UInt64,
		NativeSymbols.Int128, NativeSymbols.UInt128,
		NativeSymbols.IntSize, NativeSymbols.UIntSize,
		NativeSymbols.UntypedInteger
	];
	
	private readonly Dictionary<UnaryOperationKey, OperationImpl> _unaryOps = CreateUnaryOps();
	private readonly Dictionary<BinaryOperationKey, OperationImpl> _binaryOps = CreateBinaryOps();
	
	private readonly record struct UnaryOperationKey(TokenType Op, TypeSymbol Operand);
	private readonly record struct BinaryOperationKey(TypeSymbol Left, TokenType Op, TypeSymbol Right);
	
	private readonly ConversionTable _conversionTable = conversionTable;
	
	public bool CreateBinary(TypeSymbol left, TokenType op, TypeSymbol right, OperationImpl impl) =>
		_binaryOps.TryAdd(new(left, op, right), impl);
	
	public bool CreateUnary(TokenType op, TypeSymbol operand, OperationImpl impl) =>
		_unaryOps.TryAdd(new(op, operand), impl);
	
	public BinaryResolution ResolveBinary(TypeSymbol left, TokenType op, TypeSymbol right, TypeSymbol? target = null)
	{
		var key = new BinaryOperationKey(left, op, right);
		if (_binaryOps.TryGetValue(key, out var exact))
			return new(exact, null, null);
		
		// TODO Do implicit conversion graph traversal
		return new(null, null, null);
		
		// No exact match, try implicit conversions
		//var leftImplicit = _conversionTable.FindAllImplicit(left).ToImmutableArray();
		//var rightImplicit = _conversionTable.FindAllImplicit(right).ToImmutableArray();
	}
	
	public UnaryResolution ResolveUnary(TokenType op, TypeSymbol operand, TypeSymbol? target = null)
	{
		var key = new UnaryOperationKey(op, operand);
		if (_unaryOps.TryGetValue(key, out var exact))
			return new(exact, null);
		
		// TODO Do implicit conversion graph traversal
		return new(null, null);
		
		// No exact match, try implicit conversions
		//var leftImplicit = _conversionTable.FindAllImplicit(left).ToImmutableArray();
		//var rightImplicit = _conversionTable.FindAllImplicit(right).ToImmutableArray();
	}
	
	private static Dictionary<UnaryOperationKey, OperationImpl> CreateUnaryOps()
	{
		var result = new Dictionary<UnaryOperationKey, OperationImpl>();
		
		foreach (var type in _numericTypes)
		{
			foreach (var op in _numericUnaryOps)
				result[new(op, type)] = new NativeImpl(op, type);
		}
		
		result[new(TokenType.OpBang, NativeSymbols.Bool)] = new NativeImpl(TokenType.OpBang, NativeSymbols.Bool);
		
		return result;
	}
	
	private static Dictionary<BinaryOperationKey, OperationImpl> CreateBinaryOps()
	{
		var result = new Dictionary<BinaryOperationKey, OperationImpl>();
		
		foreach (var type in _numericTypes)
		{
			foreach (var op in _numericBinOps)
				result[new(type, op, type)] = new NativeImpl(op, type);
			
			foreach (var op in _comparisonBinOps)
				result[new(type, op, type)] = new NativeImpl(op, NativeSymbols.Bool);
		}
		
		foreach (var op in _boolBinOps)
			result[new(NativeSymbols.Bool, op, NativeSymbols.Bool)] = new NativeImpl(op, NativeSymbols.Bool);
		
		// Pointers
		result[new(NativeSymbols.VoidPtr, TokenType.OpPlus, NativeSymbols.VoidPtr)]
			= new NativeImpl(TokenType.OpPlus, NativeSymbols.VoidPtr);
		
		result[new(NativeSymbols.VoidPtr, TokenType.OpMinus, NativeSymbols.VoidPtr)]
			= new NativeImpl(TokenType.OpMinus, NativeSymbols.VoidPtr);
		
		
		foreach (var op in _comparisonBinOps)
			result[new(NativeSymbols.VoidPtr, op, NativeSymbols.VoidPtr)] = new NativeImpl(op, NativeSymbols.Bool);
		
		// TODO Should equality operations should be defined for everything?
		
		return result;
	}
}

public abstract class OperationImpl(TokenType op, TypeSymbol result)
{
	public TokenType Op { get; } = op;
	public TypeSymbol Result { get; } = result;
}

public sealed class NativeImpl(TokenType op, TypeSymbol result) : OperationImpl(op, result)
{
}

public sealed class FunctionImpl(TokenType op, FunctionInfo function) : OperationImpl(op, function.Signature.ReturnType)
{
	public FunctionInfo Function { get; } = function;
}