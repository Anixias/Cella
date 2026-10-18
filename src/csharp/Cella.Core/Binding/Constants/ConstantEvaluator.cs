using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

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
	private readonly List<LocalVariableSymbol> _patternBindings = [];
	
	private bool foldsBindings;
	private bool wrapsChecked;
	private IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? substitution;
	
	public Constant? Evaluate(IResolvedExpressionNode node)
	{
		if (_bindings.Count > 0 || foldsBindings || substitution is not null)
			return Fold(node);
		
		if (_cache.TryGetValue(node, out var cached))
			return cached;
		
		var result = Fold(node);
		_cache[node] = result;
		return result;
	}
	
	private Constant? FoldTraitConstant(ResolvedTraitConstantExpressionNode node)
	{
		var owner = substitution is null ? node.Owner : typePool.Substitute(node.Owner, substitution);
		return TypePool.ContainsTypeParameters(owner)
			? null
			: typePool.TraitConstantValue?.Invoke(owner, node.Constant);
	}
	
	public Constant? EvaluateInstance(IResolvedExpressionNode node,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		var outer = substitution;
		substitution = map;
		try
		{
			return Fold(node);
		}
		finally
		{
			substitution = outer;
		}
	}
	
	public StaticFault? FindInstanceFault(IResolvedExpressionNode node,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		var outer = substitution;
		substitution = map;
		try
		{
			return FindFault(node);
		}
		finally
		{
			substitution = outer;
		}
	}
	
	private Constant? FoldWrapping(IResolvedExpressionNode node)
	{
		wrapsChecked = true;
		try
		{
			return Fold(node);
		}
		finally
		{
			wrapsChecked = false;
		}
	}
	
	private StaticFault? FindFault(IResolvedExpressionNode node)
	{
		if (node is ResolvedGlobalExpressionNode { Symbol: { IsConstant: true } global } &&
		    getGlobalValue(global) is ParametricConstant parametric)
			return FindFault(parametric.Value);
		
		if (GetOperands(node).Select(FindFault).FirstOrDefault(static fault => fault is not null) is { } inner)
			return inner;
		
		if (Fold(node) is not (null or InvalidConstant))
			return null;
		
		var message = DescribeFault(node);
		return new StaticFault(message, node.Syntax.SourceLocation)
		{
			Wrapped = message == DivisionByZero ? null : FoldWrapping(node)
		};
	}
	
	private const string DivisionByZero = "division by zero";
	
	private string DescribeFault(IResolvedExpressionNode node) => node switch
	{
		ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpSlash or TokenType.OpPercent } } n
			when Fold(n.Right) is IntegerConstant { Value.IsZero: true } => DivisionByZero,
		ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpSlash or TokenType.OpPercent } } =>
			"division overflow",
		ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpPlus } } => "addition overflow",
		ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpMinus } } => "subtraction overflow",
		ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpStar } } =>
			"multiplication overflow",
		ResolvedUnaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpMinus } } => "negation overflow",
		ResolvedConversionExpressionNode => "conversion overflow",
		_ => "constant overflow"
	};
	
	public Constant? EvaluateStandalone(IResolvedExpressionNode node)
	{
		foldsBindings = true;
		try
		{
			return Fold(node);
		}
		finally
		{
			foldsBindings = false;
			foreach (var binding in _patternBindings)
				_bindings.Remove(binding);
			
			_patternBindings.Clear();
		}
	}
	
	public Constant? EvaluateWithoutDrops(IResolvedExpressionNode node) =>
		Evaluate(node) is { } value && !BuildsDroppedValue(node) ? value : null;
	
	private bool BuildsDroppedValue(IResolvedExpressionNode node) =>
		node is ResolvedRecordExpressionNode or ResolvedArrayExpressionNode or ResolvedEnumCaseExpressionNode &&
		typePool.NeedsDrop(node.Type) || GetOperands(node).Any(BuildsDroppedValue) ||
		node is ResolvedMatchExpressionNode match && match.Arms.Any(arm => BuildsDroppedValue(arm.Value));
	
	public static IEnumerable<IResolvedExpressionNode> GetOperands(IResolvedExpressionNode node) => node switch
	{
		ResolvedUnaryOpExpressionNode n => [n.Operand],
		ResolvedBinaryOpExpressionNode n => [n.Left, n.Right],
		ResolvedChainedExpressionNode n => n.Operands,
		ResolvedConversionExpressionNode n => [n.Source],
		ResolvedAccessExpressionNode n => [n.Target],
		ResolvedIndexerExpressionNode n => [n.Target, n.Index],
		ResolvedRecordExpressionNode n => n.Fields.Select(static f => f.Value),
		ResolvedEnumCaseExpressionNode n => n.Payload,
		ResolvedIsExpressionNode n => [n.Value],
		ResolvedMatchExpressionNode n => [n.Value],
		ResolvedArrayExpressionNode n => n.Values,
		_ => []
	};
	
	public bool Fits(BigInteger value, IntegerType type) =>
		type == NativeSymbols.Char ? IsScalarValue(value) : Wrap(value, type) == value;
	
	public static bool IsScalarValue(BigInteger value) =>
		value >= 0 && value <= MaxScalarValue && (value < FirstSurrogate || value > LastSurrogate);
	
	private const int MaxScalarValue = 0x10FFFF;
	private const int FirstSurrogate = 0xD800;
	private const int LastSurrogate = 0xDFFF;
	
	public Diagnostic? FindConstantError(IResolvedExpressionNode node)
	{
		foreach (var operand in GetOperands(node))
		{
			if (FindConstantError(operand) is { } error)
				return error;
		}
		
		var (location, message) = node switch
		{
			ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpSlash or TokenType.OpPercent } } n
				when IsZero(n.Right) => (n.Right.Syntax.SourceLocation, "Division by zero"),
			ResolvedBinaryOpExpressionNode n when FindOverflowingOperation(n) is { } operation =>
				(n.Syntax.SourceLocation, $"{operation} overflows '{n.Type.Name}'"),
			ResolvedUnaryOpExpressionNode n when OverflowsNegation(n) =>
				(n.Syntax.SourceLocation, $"Negation overflows '{n.Type.Name}'"),
			ResolvedConversionExpressionNode n when FindLostConstant(n) is var (lost, target) =>
				(n.Syntax.SourceLocation, $"'{lost}' doesn't fit in '{target.Name}'"),
			_ => (default(SourceLocation), null)
		};
		
		return message is null ? null : new Diagnostic(DiagnosticSeverity.Error, location, message);
	}
	
	public bool IsZero(IResolvedExpressionNode divisor) => Evaluate(divisor) is IntegerConstant { Value.IsZero: true };
	
	public bool HasNoSize(TypeSymbol type) => !TypePool.ContainsTypeParameters(type) &&
	                                          typePool.SizeTable.TryGetSize(type) is { } size &&
	                                          size.CountBits(pointerBitSize).IsZero;
	
	public string? FindOverflowingOperation(ResolvedBinaryOpExpressionNode node)
	{
		if (node is not { Operation: NativeImpl { Op: var op }, Type: IntegerType type } ||
		    Evaluate(node.Left) is not IntegerConstant { Value: var left } ||
		    Evaluate(node.Right) is not IntegerConstant { Value: var right })
			return null;
		
		return op switch
		{
			TokenType.OpPlus when !Fits(left + right, type) => "Addition",
			TokenType.OpMinus when !Fits(left - right, type) => "Subtraction",
			TokenType.OpStar when !Fits(left * right, type) => "Multiplication",
			TokenType.OpSlash when type.IsSigned && right == BigInteger.MinusOne && !Fits(-left, type) => "Division",
			_ => null
		};
	}
	
	public bool OverflowsNegation(ResolvedUnaryOpExpressionNode node) =>
		node is { Operation: NativeImpl { Op: TokenType.OpMinus }, Type: IntegerType type } &&
		Evaluate(node.Operand) is IntegerConstant { Value: var value } && !Fits(-value, type);
	
	public (string Value, TypeSymbol Target)? FindLostConstant(ResolvedConversionExpressionNode node)
	{
		if (node is { Type: FloatType floatTarget, Conversion: FloatConversion } &&
		    floatTarget == NativeSymbols.Float32 && Evaluate(node.Source) is FloatConstant { Value: var number } &&
		    double.IsFinite(number) && float.IsInfinity((float)number))
			return (DescribeWritten(node), floatTarget);
		
		var target = node switch
		{
			{ Type: IntegerType type, Conversion: IntegerConversion or FloatConversion or EnumConversion } => type,
			{ Type: EnumSymbol { IsExternal: true } enumType, Conversion: EnumConversion } =>
				typePool.GetTagType(enumType),
			_ => null
		};
		
		if (target is null)
			return null;
		
		string? lost = Evaluate(node.Source) switch
		{
			IntegerConstant { Value: var value } when !Fits(value, target) => value.ToString(),
			FloatConstant { Value: var value } when !FitsInteger(value, target) => DescribeWritten(node),
			EnumConstant { Type: EnumSymbol enumType, Case: var enumCase } when
				typePool.GetCaseValue(enumType, enumCase) is var tag && !Fits(tag, target) => tag.ToString(),
			_ => null
		};
		
		return lost is null ? null : (lost, target);
	}
	
	private static string DescribeWritten(ResolvedConversionExpressionNode node) =>
		(node.Syntax is CallExpressionNode { Arguments: [var argument] } ? argument : node.Source.Syntax)
		.SourceLocation.GetText().ToString();
	
	private bool FitsInteger(double value, IntegerType type) =>
		double.IsFinite(value) && Fits(new BigInteger(Math.Truncate(value)), type);
	
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
		ResolvedGlobalExpressionNode { Symbol: { IsConstant: true } global } => FoldGlobal(global),
		ResolvedTraitConstantExpressionNode n => FoldTraitConstant(n),
		ResolvedValueParameterExpressionNode n when substitution is not null &&
		                                            typePool.SubstituteParameter(n.Parameter, substitution) is
			                                            ValueArgumentType { Value: var value } => new IntegerConstant(
			n.Type, value),
		ResolvedSizeOfExpressionNode n when substitution is not null => FoldLayout(n.Target, false),
		ResolvedAlignOfExpressionNode n when substitution is not null => FoldLayout(n.Target, true),
		ResolvedAccessExpressionNode n => FoldAccess(n),
		ResolvedIndexerExpressionNode n => FoldAll([n.Target, n.Index], values => Index(values[0], values[1])),
		ResolvedRecordExpressionNode n => FoldAll(n.Fields.Select(static f => f.Value),
			values => values.Length == 0 ? new ZeroConstant(n.Type) : new RecordConstant(n.Type, values)),
		ResolvedArrayExpressionNode { Type: ArrayType type } n =>
			FoldAll(n.Values, values => new ArrayConstant(type, values)),
		ResolvedFunctionReferenceExpressionNode n => new FunctionConstant(n.Function, n.Type),
		ResolvedEnumCaseExpressionNode n => FoldAll(n.Payload, values => new EnumConstant(n.Type, n.Case, values)),
		ResolvedIsExpressionNode n when foldsBindings || !n.Pattern.HasBindings => FoldIs(n),
		ResolvedMatchExpressionNode n => FoldMatch(n),
		_ => null
	};
	
	private Constant? FoldGlobal(GlobalSymbol global) => getGlobalValue(global) switch
	{
		ParametricConstant parametric when substitution is not null => Fold(parametric.Value),
		ParametricConstant => null,
		var value => value
	};
	
	private IntegerConstant? FoldLayout(TypeSymbol target, bool isAlignment)
	{
		if (typePool.SizeTable.TryGetSize(typePool.Substitute(target, substitution!)) is not { } size)
			return null;
		
		var bits = isAlignment ? size.CountAlignmentBits(pointerBitSize) : size.CountBits(pointerBitSize);
		return new IntegerConstant(NativeSymbols.UIntSize, (bits + 7) / 8);
	}
	
	private Constant? FoldIs(ResolvedIsExpressionNode node)
	{
		var value = Evaluate(node.Value);
		if (value is null or InvalidConstant)
			return value;
		
		if (MatchPattern(node.Pattern, value) is not { } matches)
			return null;
		
		if (matches)
		{
			Bind(node.Pattern, value);
			_patternBindings.AddRange(node.Pattern.AllBindings);
		}
		
		return BoolConstant.From(matches);
	}
	
	private bool? MatchPattern(ResolvedPattern pattern, Constant value)
	{
		if (pattern.Test is { } test)
		{
			_bindings[pattern.Subject!] = value;
			var result = Evaluate(test) as BoolConstant;
			_bindings.Remove(pattern.Subject!);
			return result?.Value;
		}
		
		if (pattern is { Record: not null, TestedType: null })
		{
			for (var i = 0; i < pattern.Fields.Length; i++)
			{
				if (pattern.Elements[i] is not { } element)
					continue;
				
				if (GetField(value, pattern.Fields[i]) is not { } field ||
				    MatchPattern(element, field) is not { } matches)
					return null;
				
				if (!matches)
					return false;
			}
			
			return true;
		}
		
		if (pattern.Case is not { } enumCase || value.Type is not EnumSymbol enumType || GetTag(value) is not { } tag)
			return null;
		
		if (tag != typePool.GetCaseValue(enumType, enumCase))
			return false;
		
		for (var i = 0; i < pattern.Elements.Length; i++)
		{
			if (pattern.Elements[i] is { } element && MatchPattern(element, GetPayload(value, enumCase, i)) is not true)
				return MatchPattern(element, GetPayload(value, enumCase, i));
		}
		
		return true;
	}
	
	private Constant GetPayload(Constant value, EnumCaseSymbol enumCase, int index) => value is EnumConstant constant
		? constant.Payload[index]
		: Zero(typePool.GetPayloadTypes((EnumSymbol)value.Type, enumCase)[index]);
	
	private Constant? GetField(Constant value, FieldSymbol field) => value switch
	{
		RecordConstant record => record.Fields[typePool.GetFieldIndex(value.Type, field)],
		ZeroConstant => Zero(typePool.GetTypeOfMember(field)),
		_ => null
	};
	
	private void Bind(ResolvedPattern pattern, Constant value)
	{
		for (var i = 0; i < pattern.Bindings.Length; i++)
		{
			var slot = pattern.Case is { } enumCase ? GetPayload(value, enumCase, i)
				: pattern is { Record: not null, TestedType: null } ? GetField(value, pattern.Fields[i])
				: null;
			
			if (slot is null)
				continue;
			
			if (pattern.Elements.Length > i && pattern.Elements[i] is { } element)
				Bind(element, slot);
			
			if (pattern.Bindings[i] is { } binding)
				_bindings[binding] = slot;
		}
	}
	
	private Constant? FoldMatch(ResolvedMatchExpressionNode node)
	{
		var value = Evaluate(node.Value);
		if (value is null or InvalidConstant)
			return value;
		
		foreach (var arm in node.Arms)
		{
			ResolvedPattern? matched = null;
			foreach (var pattern in arm.Patterns)
			{
				switch (MatchPattern(pattern, value))
				{
					case null:
						return null;
					
					case true:
						matched = pattern;
						break;
				}
				
				if (matched is not null)
					break;
			}
			
			if (matched is null && !arm.Patterns.IsEmpty)
				continue;
			
			if (matched is not null)
				Bind(matched, value);
			
			var guard = arm.Guard is null ? BoolConstant.True : Evaluate(arm.Guard) as BoolConstant;
			var result = guard is { Value: true } ? Evaluate(arm.Value) : null;
			foreach (var binding in matched?.AllBindings ?? [])
				_bindings.Remove(binding);
			
			if (guard is null)
				return null;
			
			if (guard.Value)
				return result;
		}
		
		return null;
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
		FloatType or UntypedFloatType when node.Value is double value => new FloatConstant(node.Type, value),
		UntypedFloatType when node.Value is string text =>
			new FloatConstant(node.Type, double.Parse(text, CultureInfo.InvariantCulture)),
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } when node.Value is bool value => BoolConstant.From(value),
		PointerType or UntypedNullType or TypeParameterSymbol { HasNull: true } => new NullConstant(node.Type),
		PrimitiveType { Kind: PrimitiveTypeKind.CStr } when node.Value is null => new NullConstant(node.Type),
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
			(UnaryOperation.Negation, IntegerConstant value) => Checked(node.Type, -value.Value),
			(UnaryOperation.Negation, FloatConstant value) => Float(node.Type, -value.Value),
			(UnaryOperation.BitwiseNot, IntegerConstant value) => Integer(node.Type, ~value.Value),
			(UnaryOperation.LogicalNot, BoolConstant value) => BoolConstant.From(!value.Value),
			_ => null
		});
	}
	
	private Constant? FoldBinary(ResolvedBinaryOpExpressionNode node)
	{
		if (node.Operation is not { } operation)
			return null;
		
		if (foldsBindings && operation is NativeImpl native &&
		    OperationMapping.ToBinaryOperation(native.Op) is BinaryOperation.LogicalAnd or BinaryOperation.LogicalOr &&
		    Evaluate(node.Left) is BoolConstant left &&
		    left.Value == (OperationMapping.ToBinaryOperation(native.Op) == BinaryOperation.LogicalOr))
			return left;
		
		return FoldAll([node.Left, node.Right], values => Apply(operation, values[0], values[1], node.Type));
	}
	
	private Constant? FoldChain(ResolvedChainedExpressionNode node) => FoldAll(node.Operands, values =>
	{
		for (var i = 0; i < node.Links.Length; i++)
		{
			var link = node.Links[i];
			if (link.Operation is not { } operation || ConvertOperand(values[i], link.LeftConversion) is not { } left ||
			    ConvertOperand(values[i + 1], link.RightConversion) is not { } right ||
			    Apply(operation, left, right, node.Type) is not BoolConstant result)
				return null;
			
			if (!result.Value)
				return BoolConstant.False;
		}
		
		return BoolConstant.True;
	});
	
	private Constant? ConvertOperand(Constant value, Conversion? conversion) =>
		conversion is null ? value : Convert(value, conversion);
	
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
		if (index is not IntegerConstant { Value: var position } || position.Sign < 0)
			return null;
		
		if (target is StringConstant { Value: var text })
			return GetBytes(text) is { } bytes && position < bytes.Length
				? new IntegerConstant(NativeSymbols.UInt8, bytes[(int)position])
				: null;
		
		if (target.Type is not ArrayType array || position >= array.Length)
			return null;
		
		return target switch
		{
			ArrayConstant constant => constant.Elements[(int)position],
			ZeroConstant => Zero(array.ElementType),
			_ => null
		};
	}
	
	public static byte[]? GetBytes(object text) => text switch
	{
		StrValue str => str.Bytes,
		byte[] bytes => bytes,
		_ => null
	};
	
	private Constant? Apply(OperationImpl implementation, Constant left, Constant right, TypeSymbol resultType) =>
		implementation is NativeImpl native
			? Apply(OperationMapping.ToBinaryOperation(native.Op), left, right, resultType)
			: null;
	
	public Constant? Apply(BinaryOperation operation, Constant left, Constant right, TypeSymbol resultType) =>
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
			BinaryOperation.Addition => Checked(resultType, a + b),
			BinaryOperation.Subtraction => Checked(resultType, a - b),
			BinaryOperation.Multiplication => Checked(resultType, a * b),
			BinaryOperation.WrappingAddition => Integer(resultType, a + b),
			BinaryOperation.WrappingSubtraction => Integer(resultType, a - b),
			BinaryOperation.WrappingMultiplication => Integer(resultType, a * b),
			BinaryOperation.Division => b.IsZero ? null : Checked(resultType, BigInteger.Divide(a, b)),
			BinaryOperation.Modulo => b.IsZero ? null : Checked(resultType, BigInteger.Remainder(a, b)),
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
	
	private Constant? Rotate(BinaryOperation operation, IntegerConstant value, IntegerConstant amount) =>
		value.Type is IntegerType valueType
			? Integer(valueType, Rotate(value.Value, amount.Value, valueType, operation == BinaryOperation.RotateLeft))
			: null;
	
	public BigInteger Rotate(BigInteger value, BigInteger amount, IntegerType type, bool isLeft)
	{
		var bits = (int)CountBits(type);
		var mask = (BigInteger.One << bits) - 1;
		var pattern = value & mask;
		var forward = (int)Modulo(amount, bits);
		var backward = (bits - forward) % bits;
		var (left, right) = isLeft ? (forward, backward) : (backward, forward);
		return Wrap(((pattern << left) | (pattern >> right)) & mask, type);
	}
	
	[SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator",
		Justification = "Folded float comparisons must match the exact IEEE comparison the program makes at run time")]
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
	
	public Constant? Convert(Constant value, Conversion conversion) => (conversion, value) switch
	{
		(IdentityConversion, _) => value,
		(IntegerConversion c, IntegerConstant integer) => Checked(c.To, integer.Value),
		(FloatConversion { To: FloatType to }, FloatConstant number) => Float(to, number.Value),
		(FloatConversion { To: FloatType to }, IntegerConstant integer) =>
			new FloatConstant(to, IntegerToFloat(integer.Value, to)),
		(FloatConversion { To: IntegerType to }, FloatConstant number) => wrapsChecked || FitsInteger(number.Value, to)
			? new IntegerConstant(to, FloatToInteger(number.Value, to))
			: null,
		(NativeConversion { To: PointerType or StringType } c, NullConstant) => new NullConstant(c.To),
		(NativeConversion { To: PointerType or StringType } c, IntegerConstant { Value.IsZero: true }) =>
			new NullConstant(c.To),
		(NativeConversion { To: IntegerType } c, NullConstant) => new IntegerConstant(c.To, BigInteger.Zero),
		(NativeConversion { To: IntegerType } c, BoolConstant flag) =>
			new IntegerConstant(c.To, flag.Value ? BigInteger.One : BigInteger.Zero),
		(FreeConversion c, NullConstant) => new NullConstant(c.To),
		(FreeConversion c, IntegerConstant integer) when c.From == NativeSymbols.Char || c.To == NativeSymbols.Char =>
			new IntegerConstant(c.To, integer.Value),
		(FreeConversion { From: FunctionType } c, FunctionConstant function) =>
			new FunctionConstant(function.Function, c.To),
		(EnumConversion { To: IntegerType to }, var constant) when GetTag(constant) is { } tag => Checked(to, tag),
		(EnumConversion { To: EnumSymbol { IsExternal: true } enumType }, IntegerConstant integer) =>
			Checked(typePool.GetTagType(enumType), integer.Value) is IntegerConstant tag
				? ToEnum(enumType, tag.Value)
				: null,
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
	
	private Constant? Checked(TypeSymbol type, BigInteger value) =>
		wrapsChecked ? Integer(type, value)
		: type is not IntegerType integer || Fits(value, integer) ? new IntegerConstant(type, value)
		: null;
	
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
	
	public (BigInteger Min, BigInteger Max) GetLimits(IntegerType type)
	{
		var bits = (int)CountBits(type);
		return type.IsSigned
			? (-(BigInteger.One << (bits - 1)), (BigInteger.One << (bits - 1)) - 1)
			: (BigInteger.Zero, (BigInteger.One << bits) - 1);
	}
	
	public BigInteger Wrap(BigInteger value, IntegerType type)
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
	
	private uint CountBits(TypeSymbol type) => (uint)typePool.SizeTable.GetSize(type).CountBits(pointerBitSize);
}