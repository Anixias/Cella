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
		NativeSymbols.IntSize, NativeSymbols.UIntSize
	];
	
	private static readonly ImmutableDictionary<UnaryOperation, OperationImpl> _unaryOps =
		CreateUnaryOps().ToImmutableDictionary();
	
	private static readonly ImmutableDictionary<BinaryOperation, OperationImpl> _binaryOps =
		CreateBinaryOps().ToImmutableDictionary();
	
	private readonly record struct UnaryOperation(TokenType Op, TypeSymbol Operand);
	private readonly record struct BinaryOperation(TypeSymbol Left, TokenType Op, TypeSymbol Right);
	
	private readonly ConversionTable _conversionTable = conversionTable;
	
	public BinaryResolution ResolveBinary(TypeSymbol left, TokenType op, TypeSymbol right, TypeSymbol? target = null)
	{
		var key = new BinaryOperation(left, op, right);
		if (_binaryOps.TryGetValue(key, out var exact))
			return new(exact, null, null);
		
		// TODO Do implicit conversion graph traversal
		return new(null, null, null);
		
		// No exact match, try implicit conversions
		//var leftImplicit = _conversionTable.FindAllImplicit(left).ToImmutableArray();
		//var rightImplicit = _conversionTable.FindAllImplicit(right).ToImmutableArray();
	}
	
	public UnaryResolution ResolveUnary(TokenType op, TypeSymbol right, TypeSymbol? target = null)
	{
		var key = new UnaryOperation(op, right);
		if (_unaryOps.TryGetValue(key, out var exact))
			return new(exact, null);
		
		// TODO Do implicit conversion graph traversal
		return new(null, null);
		
		// No exact match, try implicit conversions
		//var leftImplicit = _conversionTable.FindAllImplicit(left).ToImmutableArray();
		//var rightImplicit = _conversionTable.FindAllImplicit(right).ToImmutableArray();
	}
	
	private static Dictionary<UnaryOperation, OperationImpl> CreateUnaryOps()
	{
		var result = new Dictionary<UnaryOperation, OperationImpl>();
		
		foreach (var type in _numericTypes)
		{
			foreach (var op in _numericUnaryOps)
				result[new(op, type)] = new NativeImpl(op, type);
		}
		
		result[new(TokenType.OpBang, NativeSymbols.Bool)] = new NativeImpl(TokenType.OpBang, NativeSymbols.Bool);
		
		return result;
	}
	
	private static Dictionary<BinaryOperation, OperationImpl> CreateBinaryOps()
	{
		var result = new Dictionary<BinaryOperation, OperationImpl>();
		
		foreach (var type in _numericTypes)
		{
			foreach (var op in _numericBinOps)
				result[new(type, op, type)] = new NativeImpl(op, type);
			
			foreach (var op in _comparisonBinOps)
				result[new(type, op, type)] = new NativeImpl(op, NativeSymbols.Bool);
		}
		
		foreach (var op in _boolBinOps)
			result[new(NativeSymbols.Bool, op, NativeSymbols.Bool)] = new NativeImpl(op, NativeSymbols.Bool);
		
		// TODO Should equality operations should be defined for everything?
		
		return result;
	}
}

public abstract class OperationImpl(TypeSymbol result)
{
	public TypeSymbol Result { get; } = result;
}

public sealed class NativeImpl(TokenType op, TypeSymbol result) : OperationImpl(result)
{
	public TokenType Op { get; } = op;
}

public sealed class FunctionImpl(FunctionInfo function) : OperationImpl(function.Signature.ReturnType)
{
	public FunctionInfo Function { get; } = function;
}