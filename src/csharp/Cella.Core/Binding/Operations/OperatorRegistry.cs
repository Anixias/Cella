using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Operations;

public sealed class OperatorRegistry
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
		.._equalityBinOps
	];
	
	private static readonly ImmutableArray<TokenType> _numericUnaryOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus,
		TokenType.OpTilde,
	];
	
	private static readonly ImmutableArray<TokenType> _boolBinOps =
	[
		TokenType.OpAmpersandEqual,
		TokenType.OpAmpersandAmpersand,
		TokenType.OpAmpersand,
		TokenType.OpBarEqual,
		TokenType.OpBarBar,
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
		NativeSymbols.Char // TODO Handle char differently?
	];
	
	private readonly Dictionary<TokenType, List<ICallable>> _unaryOps = CreateUnaryOps();
	private readonly Dictionary<TokenType, List<ICallable>> _binaryOps;
	
	public OperatorRegistry()
	{
		_binaryOps = CreateBinaryOps();
	}
	
	public void Create(OperationImpl impl)
	{
		switch (impl.ParameterTypes.Length)
		{
			case 1:
				_unaryOps.GetOrAdd(impl.Op).Add(impl);
				break;
			
			case 2:
				_binaryOps.GetOrAdd(impl.Op).Add(impl);
				break;
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private static Dictionary<TokenType, List<ICallable>> CreateUnaryOps()
	{
		var result = new Dictionary<TokenType, List<ICallable>>();
		
		foreach (var op in _numericUnaryOps)
		{
			var list = result.GetOrAdd(op);
			foreach (var type in _numericTypes)
				list.Add(new NativeImpl(op, type, type));
		}
		
		result.GetOrAdd(TokenType.OpBang).Add(new NativeImpl(TokenType.OpBang, NativeSymbols.Bool, NativeSymbols.Bool));
		return result;
	}
	
	private Dictionary<TokenType, List<ICallable>> CreateBinaryOps()
	{
		var result = new Dictionary<TokenType, List<ICallable>>();
		
		
		foreach (var op in _numericBinOps)
		{
			var list = result.GetOrAdd(op);
			foreach (var type in _numericTypes)
				list.Add(new NativeImpl(op, type, type, type));
		}
		
		foreach (var op in _comparisonBinOps)
		{
			var list = result.GetOrAdd(op);
			foreach (var type in _numericTypes)
				list.Add(new NativeImpl(op, NativeSymbols.Bool, type, type));
		}
		
		foreach (var op in _boolBinOps)
			result.GetOrAdd(op).Add(new NativeImpl(op, NativeSymbols.Bool, NativeSymbols.Bool, NativeSymbols.Bool));
		
		// Pointers
		foreach (var op in _comparisonBinOps)
			result.GetOrAdd(op)
				.Add(new NativeImpl(op, NativeSymbols.Bool, NativeSymbols.VoidPtr, NativeSymbols.VoidPtr));
		
		// TODO Should equality operations should be defined for everything?
		
		return result;
	}
	
	public IReadOnlyList<ICallable> GetUnaryCandidates(TokenType op) =>
		_unaryOps.TryGetValue(op, out var candidates) ? candidates : Array.Empty<ICallable>();
	
	public IReadOnlyList<ICallable> GetBinaryCandidates(TokenType op) =>
		_binaryOps.TryGetValue(op, out var candidates) ? candidates : Array.Empty<ICallable>();
}

public abstract class OperationImpl(TokenType op, TypeSymbol result, params IEnumerable<TypeSymbol> parameters)
	: ICallable
{
	public TokenType Op { get; } = op;
	public TypeSymbol ReturnType { get; } = result;
	public ImmutableArray<TypeSymbol> ParameterTypes { get; } = parameters.ToImmutableArray();
}

public sealed class NativeImpl(TokenType op, TypeSymbol result, params IEnumerable<TypeSymbol> parameters)
	: OperationImpl(op, result, parameters);

public sealed class FunctionImpl(TokenType op, FunctionInfo function)
	: OperationImpl(op, function.Signature.ReturnType, function.Signature.ParameterTypes)
{
	public FunctionInfo Function { get; } = function;
}

public sealed class PointerOffsetImpl(TokenType op, PointerType pointerType, TypeSymbol offsetType)
	: OperationImpl(op, pointerType, pointerType, offsetType)
{
	public PointerType PointerType { get; } = pointerType;
	public TypeSymbol OffsetType { get; } = offsetType;
}

public sealed class PointerDifferenceImpl(PointerType pointerType)
	: OperationImpl(TokenType.OpMinus, NativeSymbols.IntSize, pointerType, pointerType)
{
	public PointerType PointerType { get; } = pointerType;
}

public sealed class ConversionImpl(TokenType op, TypeSymbol result,
	params IReadOnlyList<(TypeSymbol Type, Conversion? Conversion)> parameters)
	: OperationImpl(op, result, parameters.Select(static p => p.Type))
{
	public ImmutableArray<Conversion?> ParameterConversions { get; } = parameters.Select(static p => p.Conversion)
		.ToImmutableArray();
	
	public Conversion? ResultConversion { get; init; }
}