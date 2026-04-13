using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Operations;

public readonly record struct BinaryResolution
(
	OperationImpl Operation,
	Conversion? LeftConversion,
	Conversion? RightConversion
);

public readonly struct BinaryResolutionSet
{
	public static BinaryResolutionSet None => default;
	public static BinaryResolutionSet Single(BinaryResolution resolution) => new(resolution, default, 1);
	public static BinaryResolutionSet Ambiguous(BinaryResolution a, BinaryResolution b) => new(a, b, 2);
	
	public int Count { get; }
	public bool IsAmbiguous => Count > 1;
	public bool HasResult => Count > 0;
	
	private readonly BinaryResolution _first;
	private readonly BinaryResolution _second;
	
	public BinaryResolution this[int index] => index switch
	{
		0 when Count > 0 => _first,
		1 when Count > 1 => _second,
		_ => throw new ArgumentOutOfRangeException(nameof(index))
	};
	
	private BinaryResolutionSet(BinaryResolution first, BinaryResolution second, int count)
	{
		_first = first;
		_second = second;
		Count = count;
	}
}

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
	
	public BinaryResolutionSet ResolveBinary(TypeSymbol left, TokenType op, TypeSymbol right)
	{
		var key = new BinaryOperationKey(left, op, right);
		if (_binaryOps.TryGetValue(key, out var exact))
			return BinaryResolutionSet.Single(new(exact, null, null));
		
		if (left == right)
			return BinaryResolutionSet.None;
		
		BinaryResolution? leftCandidate = null;
		var leftCost = int.MaxValue;
		BinaryResolution? rightCandidate = null;
		var rightCost = int.MaxValue;
		
		var leftConversion = _conversionTable.FindImplicit(left, right);
		if (leftConversion is not null && _binaryOps.TryGetValue(new(right, op, right), out var impl))
		{
			leftCandidate = new(impl, leftConversion, null);
			leftCost = leftConversion.Cost;
		}
		
		var rightConversion = _conversionTable.FindImplicit(right, left);
		if (rightConversion is not null && _binaryOps.TryGetValue(new(left, op, left), out impl))
		{
			rightCandidate = new(impl, null, rightConversion);
			rightCost = rightConversion.Cost;
		}
		
		if (leftCandidate is null && rightCandidate is null)
			return BinaryResolutionSet.None;
		
		if (leftCandidate is not null && rightCandidate is not null)
		{
			if (leftCost < rightCost)
				return BinaryResolutionSet.Single(leftCandidate.Value);
			if (rightCost < leftCost)
				return BinaryResolutionSet.Single(rightCandidate.Value);
			
			return BinaryResolutionSet.Ambiguous(leftCandidate.Value, rightCandidate.Value);
		}
		
		return BinaryResolutionSet.Single(leftCandidate ?? rightCandidate!.Value);
	}
	
	public OperationImpl? ResolveUnary(TokenType op, TypeSymbol operand) =>
		_unaryOps.GetValueOrDefault(new(op, operand));
	
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