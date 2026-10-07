using System.Globalization;
using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Constants;

public sealed class ConstantEvaluator
(
	TypePool typePool,
	uint pointerBitSize,
	Func<GlobalSymbol, Constant?> getGlobalValue
)
{
	private readonly Dictionary<IResolvedExpressionNode, Constant?> _cache = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<LocalVariableSymbol, Constant> _bindings = [];
	
	public Constant? Evaluate(IResolvedExpressionNode node)
	{
		if (_bindings.Count > 0)
			return Fold(node);
		
		if (_cache.TryGetValue(node, out var cached))
			return cached;
		
		var result = Fold(node);
		_cache[node] = result;
		return result;
	}
	
	public bool Fits(BigInteger value, IntegerType type) => Wrap(value, type) == value;
	
	private Constant? Fold(IResolvedExpressionNode node) => node switch
	{
		ResolvedInvalidExpressionNode => InvalidConstant.Instance,
		ResolvedLiteralExpressionNode n => FoldLiteral(n),
		ResolvedUnaryOpExpressionNode n => FoldUnary(n),
		ResolvedBinaryOpExpressionNode n => FoldBinary(n),
		ResolvedChainedExpressionNode n => FoldChain(n),
		ResolvedConversionExpressionNode n => FoldAll([n.Source], values => Convert(values[0], n.Conversion)),
		ResolvedVarExpressionNode { Symbol: LocalVariableSymbol symbol } when
			_bindings.TryGetValue(symbol, out var bound) => bound,
		ResolvedVarExpressionNode { Symbol: LocalVariableSymbol { ConstantValue: { } value } } => value,
		ResolvedGlobalExpressionNode { Symbol: { IsMutable: false } global } => getGlobalValue(global),
		ResolvedAccessExpressionNode n => FoldAccess(n),
		ResolvedIndexerExpressionNode n => FoldAll([n.Target, n.Index], values => Index(values[0], values[1])),
		ResolvedRecordExpressionNode n => FoldAll(n.Fields.Select(static f => f.Value),
			values => values.Length == 0 ? new ZeroConstant(n.Type) : new RecordConstant(n.Type, values)),
		ResolvedArrayExpressionNode { Type: ArrayType type } n =>
			FoldAll(n.Values, values => new ArrayConstant(type, values)),
		ResolvedFunctionReferenceExpressionNode n => new FunctionConstant(n.Function, n.Type),
		ResolvedEnumCaseExpressionNode n => FoldAll(n.Payload, values => new EnumConstant(n.Type, n.Case, values)),
		ResolvedIsExpressionNode { Pattern.HasBindings: false } n => FoldAll([n.Value], values =>
			values[0].Type is EnumSymbol enumType && GetTag(values[0]) is { } tag
				? BoolConstant.From(tag == typePool.GetCaseValue(enumType, n.Pattern.Case))
				: null),
		ResolvedMatchExpressionNode n => FoldMatch(n),
		_ => null
	};
	
	private Constant? FoldMatch(ResolvedMatchExpressionNode node)
	{
		var value = Evaluate(node.Value);
		if (value is null or InvalidConstant)
			return value;
		
		if (value.Type is not EnumSymbol enumType || GetTag(value) is not { } tag || node.Arms.FirstOrDefault(arm =>
			    arm.Pattern is null || typePool.GetCaseValue(enumType, arm.Pattern.Case) == tag) is not { } arm)
			return null;
		
		var bindings = arm.Pattern?.Bindings ?? [];
		for (var i = 0; i < bindings.Length; i++)
		{
			if (bindings[i] is { } binding)
				_bindings[binding] = value is EnumConstant constant ? constant.Payload[i] : Zero(binding.Type);
		}
		
		var result = Evaluate(arm.Value);
		foreach (var binding in bindings.OfType<LocalVariableSymbol>())
			_bindings.Remove(binding);
		
		return result;
	}
	
	private BigInteger? GetTag(Constant constant) => constant switch
	{
		EnumConstant { Type: EnumSymbol enumType } value => typePool.GetCaseValue(enumType, value.Case),
		EnumTagConstant { Type: EnumSymbol { IsMatch: true } enumType } value =>
			typePool.FindCase(enumType, value.Tag)?.Index,
		EnumTagConstant value => value.Tag,
		ZeroConstant { Type: EnumSymbol { IsMatch: true } enumType } =>
			typePool.FindCase(enumType, BigInteger.Zero)?.Index,
		ZeroConstant { Type: EnumSymbol } => BigInteger.Zero,
		_ => null
	};
	
	public static BigInteger? GetMatchValue(Constant constant) => constant switch
	{
		IntegerConstant integer => integer.Value,
		BoolConstant flag => flag.Value ? BigInteger.One : BigInteger.Zero,
		NullConstant or ZeroConstant => BigInteger.Zero,
		_ => null
	};
	
	private Constant? FoldAll(IEnumerable<IResolvedExpressionNode> operands, Func<Constant[], Constant?> fold)
	{
		var values = new List<Constant>();
		var isConstant = true;
		
		foreach (var operand in operands)
		{
			switch (Evaluate(operand))
			{
				case InvalidConstant:
					return InvalidConstant.Instance;
				
				case { } value:
					values.Add(value);
					break;
				
				default:
					isConstant = false;
					break;
			}
		}
		
		return isConstant ? fold(values.ToArray()) : null;
	}
	
	private static Constant? FoldLiteral(ResolvedLiteralExpressionNode node) => node.Type switch
	{
		IntegerType or UntypedIntegerType when node.IntegerValue is { } value => new IntegerConstant(node.Type, value),
		FloatType when node.Value is double value => new FloatConstant(node.Type, value),
		UntypedFloatType when node.Value is string text =>
			new FloatConstant(node.Type, double.Parse(text, CultureInfo.InvariantCulture)),
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } when node.Value is bool value => BoolConstant.From(value),
		PointerType or UntypedNullType => new NullConstant(node.Type),
		StringType or UntypedStringType when node.Value is { } value => new StringConstant(node.Type, value),
		InvalidType => InvalidConstant.Instance,
		_ => null
	};
	
	private Constant? FoldUnary(ResolvedUnaryOpExpressionNode node)
	{
		if (node.Operation is not NativeImpl native)
			return null;
		
		var operation = OperationMapping.ToUnaryOperation(native.Op);
		if (operation is UnaryOperation.AddressOf or UnaryOperation.Dereference)
			return null;
		
		return FoldAll([node.Operand], values => (operation, values[0]) switch
		{
			(UnaryOperation.Identity, var value) => value,
			(UnaryOperation.Negation, IntegerConstant value) => Integer(node.Type, -value.Value),
			(UnaryOperation.Negation, FloatConstant value) => Float(node.Type, -value.Value),
			(UnaryOperation.BitwiseNot, IntegerConstant value) => Integer(node.Type, ~value.Value),
			(UnaryOperation.LogicalNot, BoolConstant value) => BoolConstant.From(!value.Value),
			_ => null
		});
	}
	
	private Constant? FoldBinary(ResolvedBinaryOpExpressionNode node) => node.Operation is { } operation
		? FoldAll([node.Left, node.Right], values => Apply(operation, values[0], values[1], node.Type))
		: null;
	
	private Constant? FoldChain(ResolvedChainedExpressionNode node) => FoldAll(node.Operands, values =>
	{
		for (var i = 0; i < node.Links.Length; i++)
		{
			if (node.Links[i].Operation is not { } operation ||
			    Apply(operation, values[i], values[i + 1], node.Type) is not BoolConstant result)
				return null;
			
			if (!result.Value)
				return BoolConstant.False;
		}
		
		return BoolConstant.True;
	});
	
	private Constant? FoldAccess(ResolvedAccessExpressionNode node)
	{
		switch (node.Member)
		{
			case PropertySymbol { Getter: NativeAccessor { Intrinsic: NativeMemberIntrinsic.ArrayLength } }
				when node.Target.Type is ArrayType { Length.Sign: >= 0 } array:
				return new IntegerConstant(node.Type, array.Length);
			
			case PropertySymbol { Getter: NativeAccessor { Intrinsic: NativeMemberIntrinsic.StrByteLength } }:
				return FoldAll([node.Target], values => values[0] is StringConstant { Value: StrValue text }
					? new IntegerConstant(node.Type, text.Length)
					: null);
			
			case FieldSymbol field:
				return FoldAll([node.Target], values => values[0] switch
				{
					RecordConstant record => record.Fields[typePool.GetFieldIndex(node.Target.Type, field)],
					ZeroConstant => Zero(node.Type),
					_ => null
				});
			
			default:
				return null;
		}
	}
	
	private static Constant? Index(Constant target, Constant index)
	{
		if (index is not IntegerConstant { Value: var position } || target.Type is not ArrayType array ||
		    position.Sign < 0 || position >= array.Length)
			return null;
		
		return target switch
		{
			ArrayConstant constant => constant.Elements[(int)position],
			ZeroConstant => Zero(array.ElementType),
			_ => null
		};
	}
	
	private Constant? Apply(OperationImpl implementation, Constant left, Constant right, TypeSymbol resultType)
	{
		switch (implementation)
		{
			case NativeImpl native:
				return Apply(OperationMapping.ToBinaryOperation(native.Op), left, right, resultType);
			
			case ConversionImpl conversion:
			{
				var convertedLeft = conversion.ParameterConversions[0] is { } leftConversion
					? Convert(left, leftConversion)
					: left;
				
				var convertedRight = conversion.ParameterConversions[1] is { } rightConversion
					? Convert(right, rightConversion)
					: right;
				
				if (convertedLeft is null || convertedRight is null)
					return null;
				
				var intermediateType = conversion.ResultConversion?.From ?? conversion.ReturnType;
				var result = Apply(OperationMapping.ToBinaryOperation(conversion.Op), convertedLeft, convertedRight,
					intermediateType);
				
				return result is not null && conversion.ResultConversion is { } resultConversion
					? Convert(result, resultConversion)
					: result;
			}
			
			default:
				return null;
		}
	}
	
	private Constant? Apply(BinaryOperation operation, Constant left, Constant right, TypeSymbol resultType) =>
		(left, right) switch
		{
			(IntegerConstant l, IntegerConstant r) => ApplyInteger(operation, l, r, resultType),
			(FloatConstant l, FloatConstant r) => ApplyFloat(operation, l.Value, r.Value, resultType),
			(BoolConstant l, BoolConstant r) => ApplyBool(operation, l.Value, r.Value),
			(NullConstant, NullConstant) => operation switch
			{
				BinaryOperation.Equal => BoolConstant.True,
				BinaryOperation.NotEqual => BoolConstant.False,
				_ => null
			},
			_ when GetTag(left) is { } leftTag && GetTag(right) is { } rightTag => operation switch
			{
				BinaryOperation.Equal => BoolConstant.From(leftTag == rightTag),
				BinaryOperation.NotEqual => BoolConstant.From(leftTag != rightTag),
				_ => null
			},
			_ => null
		};
	
	private Constant? ApplyInteger(BinaryOperation operation, IntegerConstant left, IntegerConstant right,
		TypeSymbol resultType)
	{
		var (a, b) = (left.Value, right.Value);
		return operation switch
		{
			BinaryOperation.Addition => Integer(resultType, a + b),
			BinaryOperation.Subtraction => Integer(resultType, a - b),
			BinaryOperation.Multiplication => Integer(resultType, a * b),
			BinaryOperation.Division => b.IsZero ? null : Exact(resultType, BigInteger.Divide(a, b)),
			BinaryOperation.Modulo => b.IsZero ? null : Exact(resultType, BigInteger.Remainder(a, b)),
			BinaryOperation.Equal => BoolConstant.From(a == b),
			BinaryOperation.NotEqual => BoolConstant.From(a != b),
			BinaryOperation.Greater => BoolConstant.From(a > b),
			BinaryOperation.GreaterEqual => BoolConstant.From(a >= b),
			BinaryOperation.Less => BoolConstant.From(a < b),
			BinaryOperation.LessEqual => BoolConstant.From(a <= b),
			BinaryOperation.BitwiseAnd => Integer(resultType, a & b),
			BinaryOperation.BitwiseOr => Integer(resultType, a | b),
			BinaryOperation.BitwiseXor => Integer(resultType, a ^ b),
			BinaryOperation.ShiftLeft or BinaryOperation.ShiftRight => Shift(operation, left, right),
			BinaryOperation.RotateLeft or BinaryOperation.RotateRight => Rotate(operation, left, right),
			_ => null
		};
	}
	
	private Constant? Shift(BinaryOperation operation, IntegerConstant value, IntegerConstant amount)
	{
		if (value.Type is not IntegerType valueType || amount.Type is not IntegerType amountType)
			return null;
		
		var bits = (int)CountBits(valueType);
		var amountBits = (int)CountBits(amountType);
		var count = Modulo(amount.Value, BigInteger.One << bits);
		var checkedCount = amountBits > bits ? Modulo(amount.Value, BigInteger.One << amountBits) : count;
		
		if (checkedCount >= bits)
		{
			var fill = operation == BinaryOperation.ShiftRight && value.Value.Sign < 0
				? BigInteger.MinusOne
				: BigInteger.Zero;
			
			return Integer(valueType, fill);
		}
		
		return operation == BinaryOperation.ShiftLeft
			? Integer(valueType, value.Value << (int)count)
			: Integer(valueType, value.Value >> (int)count);
	}
	
	private Constant? Rotate(BinaryOperation operation, IntegerConstant value, IntegerConstant amount)
	{
		if (value.Type is not IntegerType valueType)
			return null;
		
		var bits = (int)CountBits(valueType);
		var mask = (BigInteger.One << bits) - 1;
		var pattern = value.Value & mask;
		var forward = (int)Modulo(amount.Value, bits);
		var backward = (bits - forward) % bits;
		var (left, right) = operation == BinaryOperation.RotateLeft ? (forward, backward) : (backward, forward);
		return Integer(valueType, ((pattern << left) | (pattern >> right)) & mask);
	}
	
	private static Constant? ApplyFloat(BinaryOperation operation, double left, double right, TypeSymbol resultType) =>
		operation switch
		{
			BinaryOperation.Addition => Float(resultType, left + right),
			BinaryOperation.Subtraction => Float(resultType, left - right),
			BinaryOperation.Multiplication => Float(resultType, left * right),
			BinaryOperation.Division => Float(resultType, left / right),
			BinaryOperation.Modulo => Float(resultType, left % right),
			BinaryOperation.Equal => BoolConstant.From(left == right),
			BinaryOperation.NotEqual => BoolConstant.From(left != right),
			BinaryOperation.Greater => BoolConstant.From(left > right),
			BinaryOperation.GreaterEqual => BoolConstant.From(left >= right),
			BinaryOperation.Less => BoolConstant.From(left < right),
			BinaryOperation.LessEqual => BoolConstant.From(left <= right),
			_ => null
		};
	
	private static Constant? ApplyBool(BinaryOperation operation, bool left, bool right) => operation switch
	{
		BinaryOperation.Equal => BoolConstant.From(left == right),
		BinaryOperation.NotEqual => BoolConstant.From(left != right),
		BinaryOperation.BitwiseAnd or BinaryOperation.LogicalAnd => BoolConstant.From(left && right),
		BinaryOperation.BitwiseOr or BinaryOperation.LogicalOr => BoolConstant.From(left || right),
		BinaryOperation.BitwiseXor => BoolConstant.From(left ^ right),
		_ => null
	};
	
	private Constant? Convert(Constant value, Conversion conversion) => (conversion, value) switch
	{
		(IdentityConversion, _) => value,
		(IntegerConversion c, IntegerConstant integer) => Integer(c.To, integer.Value),
		(FloatConversion { To: FloatType to }, FloatConstant number) => Float(to, number.Value),
		(FloatConversion { To: FloatType to }, IntegerConstant integer) =>
			new FloatConstant(to, IntegerToFloat(integer.Value, to)),
		(FloatConversion { To: IntegerType to }, FloatConstant number) =>
			new IntegerConstant(to, FloatToInteger(number.Value, to)),
		(NativeConversion { To: PointerType or StringType } c, NullConstant) => new NullConstant(c.To),
		(NativeConversion { To: PointerType or StringType } c, IntegerConstant { Value.IsZero: true }) =>
			new NullConstant(c.To),
		(NativeConversion { To: IntegerType } c, NullConstant) => new IntegerConstant(c.To, BigInteger.Zero),
		(NativeConversion { To: IntegerType } c, BoolConstant flag) =>
			new IntegerConstant(c.To, flag.Value ? BigInteger.One : BigInteger.Zero),
		(FreeConversion c, NullConstant) => new NullConstant(c.To),
		(FreeConversion { From: FunctionType } c, FunctionConstant function) =>
			new FunctionConstant(function.Function, c.To),
		(EnumConversion { To: IntegerType to }, var constant) when GetTag(constant) is { } tag => Integer(to, tag),
		(EnumConversion { To: EnumSymbol { IsExternal: true } enumType }, IntegerConstant integer) =>
			ToEnum(enumType, Wrap(integer.Value, typePool.GetTagType(enumType))),
		(EnumConversion { To: EnumSymbol enumType }, IntegerConstant integer) =>
			typePool.FindCase(enumType, integer.Value) is { } enumCase
				? new EnumConstant(enumType, enumCase, [])
				: null,
		(MatchConversion { To: EnumSymbol enumType }, _) => ToMatchEnum(enumType, value),
		(MatchConversion { From: EnumSymbol enumType } c, _) => FromMatchEnum(enumType, c.To, value),
		_ => null
	};
	
	private Constant? ToMatchEnum(EnumSymbol enumType, Constant value)
	{
		var raw = GetMatchValue(value);
		var enumCase = raw is { } listed ? typePool.FindCase(enumType, listed) : TypePool.GetElseCase(enumType);
		return enumCase switch
		{
			null => null,
			{ Fields.IsEmpty: false } => new EnumConstant(enumType, enumCase, [value]),
			_ => raw is { } stored ? new EnumTagConstant(enumType, stored) : null
		};
	}
	
	private Constant? FromMatchEnum(EnumSymbol enumType, TypeSymbol matchedType, Constant value) => value switch
	{
		EnumConstant { Payload: [var payload] } => payload,
		EnumConstant constant => typePool.GetMatchValues(enumType, constant.Case) is [var first, ..]
			? MatchConstant(matchedType, first)
			: null,
		EnumTagConstant constant => MatchConstant(matchedType, constant.Tag),
		ZeroConstant => Zero(matchedType),
		_ => null
	};
	
	private static Constant MatchConstant(TypeSymbol type, BigInteger value) => type switch
	{
		IntegerType => new IntegerConstant(type, value),
		_ when type == NativeSymbols.Bool => BoolConstant.From(!value.IsZero),
		_ => new NullConstant(type)
	};
	
	private Constant ToEnum(EnumSymbol enumType, BigInteger tag) => typePool.FindCase(enumType, tag) is { } enumCase
		? new EnumConstant(enumType, enumCase, [])
		: new EnumTagConstant(enumType, tag);
	
	private IntegerConstant Integer(TypeSymbol type, BigInteger value) =>
		new(type, type is IntegerType integer ? Wrap(value, integer) : value);
	
	private Constant? Exact(TypeSymbol type, BigInteger value) =>
		type is not IntegerType integer || Fits(value, integer) ? new IntegerConstant(type, value) : null;
	
	private static FloatConstant Float(TypeSymbol type, double value) =>
		new(type, type == NativeSymbols.Float32 ? (float)value : value);
	
	private static Constant Zero(TypeSymbol type) => type switch
	{
		IntegerType => new IntegerConstant(type, BigInteger.Zero),
		FloatType => new FloatConstant(type, 0),
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } => BoolConstant.False,
		PointerType or FunctionType or PrimitiveType { Kind: PrimitiveTypeKind.CStr } => new NullConstant(type),
		_ => new ZeroConstant(type)
	};
	
	private BigInteger Wrap(BigInteger value, IntegerType type)
	{
		var modulus = BigInteger.One << (int)CountBits(type);
		var result = Modulo(value, modulus);
		return type.IsSigned && result >= modulus >> 1 ? result - modulus : result;
	}
	
	private static BigInteger Modulo(BigInteger value, BigInteger modulus)
	{
		var result = BigInteger.Remainder(value, modulus);
		return result.Sign < 0 ? result + modulus : result;
	}
	
	private static double IntegerToFloat(BigInteger value, FloatType type)
	{
		var precision = type == NativeSymbols.Float32 ? 24 : 53;
		var magnitude = BigInteger.Abs(value);
		var bitLength = (int)magnitude.GetBitLength();
		
		double result;
		if (bitLength <= precision)
		{
			result = (double)magnitude;
		}
		else
		{
			var shift = bitLength - precision;
			var kept = magnitude >> shift;
			var remainder = magnitude - (kept << shift);
			var half = BigInteger.One << (shift - 1);
			if (remainder > half || (remainder == half && !kept.IsEven))
				kept += 1;
			
			result = Math.ScaleB((double)kept, shift);
		}
		
		result = value.Sign < 0 ? -result : result;
		return type == NativeSymbols.Float32 ? (float)result : result;
	}
	
	private BigInteger FloatToInteger(double value, IntegerType type)
	{
		var bits = (int)CountBits(type);
		var (minimum, maximum) = type.IsSigned
			? (-(BigInteger.One << (bits - 1)), (BigInteger.One << (bits - 1)) - 1)
			: (BigInteger.Zero, (BigInteger.One << bits) - 1);
		
		if (double.IsNaN(value))
			return BigInteger.Zero;
		
		if (double.IsInfinity(value))
			return value > 0 ? maximum : minimum;
		
		return BigInteger.Clamp(new BigInteger(Math.Truncate(value)), minimum, maximum);
	}
	
	private uint CountBits(TypeSymbol type) => typePool.SizeTable.GetSize(type).CountBits(pointerBitSize);
}