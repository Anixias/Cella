using System.Collections.Immutable;
using Cella.Core.Collections;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Operations;

public sealed class OperatorRegistry
{
	private static readonly ImmutableArray<TokenType> _arithmeticBinOps =
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
	];
	
	private static readonly ImmutableArray<TokenType> _wrappingBinOps =
	[
		TokenType.OpPlusPercent,
		TokenType.OpPlusPercentEqual,
		TokenType.OpMinusPercent,
		TokenType.OpMinusPercentEqual,
		TokenType.OpStarPercent,
		TokenType.OpStarPercentEqual,
	];
	
	private static readonly ImmutableArray<TokenType> _numericBinOps =
	[
		.._arithmeticBinOps,
		.._wrappingBinOps,
		TokenType.OpAmpersand,
		TokenType.OpAmpersandEqual,
		TokenType.OpBar,
		TokenType.OpBarEqual,
		TokenType.OpHat,
		TokenType.OpHatEqual,
	];
	
	private static readonly ImmutableArray<TokenType> _shiftBinOps =
	[
		TokenType.OpLessLess,
		TokenType.OpLessLessEqual,
		TokenType.OpGreaterGreater,
		TokenType.OpGreaterGreaterEqual,
		TokenType.OpLessLessLess,
		TokenType.OpLessLessLessEqual,
		TokenType.OpGreaterGreaterGreater,
		TokenType.OpGreaterGreaterGreaterEqual,
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
	
	private static readonly ImmutableArray<TokenType> _signUnaryOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus,
	];
	
	private static readonly ImmutableArray<TokenType> _numericUnaryOps =
	[
		.._signUnaryOps,
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
	
	private static readonly ImmutableArray<TypeSymbol> _numericTypes = [..NativeSymbols.PureIntegerTypes];
	
	private static readonly ImmutableArray<TypeSymbol> _comparableTypes =
	[
		.._numericTypes,
		NativeSymbols.Char,
		..NativeSymbols.FloatTypes
	];
	
	private readonly Dictionary<TokenType, List<ICallable>> _unaryOps = CreateUnaryOps();
	private readonly Dictionary<TokenType, List<ICallable>> _binaryOps;
	
	public OperatorRegistry()
	{
		_binaryOps = CreateBinaryOps();
	}
	
	internal void Save(StateSnapshot snapshot)
	{
		snapshot.SaveNested(_unaryOps);
		snapshot.SaveNested(_binaryOps);
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
			{
				if (op == TokenType.OpMinus && type is IntegerType { IsSigned: false })
					continue;
				
				list.Add(new NativeImpl(op, type, type));
			}
		}
		
		foreach (var op in _signUnaryOps)
		{
			var list = result.GetOrAdd(op);
			foreach (var type in NativeSymbols.FloatTypes)
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
		
		foreach (var op in _arithmeticBinOps)
		{
			var list = result.GetOrAdd(op);
			foreach (var type in NativeSymbols.FloatTypes)
				list.Add(new NativeImpl(op, type, type, type));
		}
		
		foreach (var op in _shiftBinOps)
		{
			var list = result.GetOrAdd(op);
			foreach (var type in NativeSymbols.PureIntegerTypes)
			{
				foreach (var amountType in NativeSymbols.PureIntegerTypes)
					list.Add(new NativeImpl(op, type, type, amountType));
			}
		}
		
		foreach (var op in _comparisonBinOps)
		{
			var list = result.GetOrAdd(op);
			foreach (var type in _comparableTypes)
				list.Add(new NativeImpl(op, NativeSymbols.Bool, type, type));
		}
		
		foreach (var op in _boolBinOps)
			result.GetOrAdd(op).Add(new NativeImpl(op, NativeSymbols.Bool, NativeSymbols.Bool, NativeSymbols.Bool));
		
		foreach (var op in new[]
		         {
			         TokenType.OpPlus, TokenType.OpPlusEqual, TokenType.OpMinus, TokenType.OpMinusEqual
		         })
			result.GetOrAdd(op).Add(new NativeImpl(op, NativeSymbols.Char, NativeSymbols.Char, NativeSymbols.Int32));
		
		result.GetOrAdd(TokenType.OpPlus)
			.Add(new NativeImpl(TokenType.OpPlus, NativeSymbols.Char, NativeSymbols.Int32, NativeSymbols.Char));
		
		result.GetOrAdd(TokenType.OpMinus)
			.Add(new NativeImpl(TokenType.OpMinus, NativeSymbols.Int32, NativeSymbols.Char, NativeSymbols.Char));
		
		// Pointers
		foreach (var op in _comparisonBinOps)
			result.GetOrAdd(op)
				.Add(new NativeImpl(op, NativeSymbols.Bool, NativeSymbols.VoidPtr, NativeSymbols.VoidPtr));
		
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

public sealed class PointerOffsetImpl
	(TokenType op, PointerType pointerType, TypeSymbol offsetType, bool isReversed = false)
	: OperationImpl(op, pointerType, isReversed ? [offsetType, pointerType] : [pointerType, offsetType])
{
	public PointerType PointerType { get; } = pointerType;
	public bool IsReversed { get; } = isReversed;
}

public sealed class PointerDifferenceImpl(PointerType pointerType)
	: OperationImpl(TokenType.OpMinus, NativeSymbols.IntSize, pointerType, pointerType)
{
	public PointerType PointerType { get; } = pointerType;
}