using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private const string UnsignedSuffix = "u";
	
	private static string DescribeLiteralKind(TokenType type) => type switch
	{
		TokenType.FloatLiteral => "float",
		TokenType.CharLiteral => "char",
		_ => "string"
	};
	
	public IResolvedExpressionNode Visit(LiteralExpressionNode node)
	{
		if (node.Token.Suffix is { } suffix)
			return ResolveSuffixedLiteral(node, suffix);
		
		var valueSpan = node.Token.AsSpan();
		
		TypeSymbol? type = null;
		object? value = null;
		
		var tokenType = node.Token.Type;
		(type, value) = tokenType switch
		{
			TokenType.IntegerLiteral => ParseInteger(valueSpan),
			TokenType.FloatLiteral => ParseFloat(valueSpan),
			TokenType.KeywordNull => ParseNull(),
			TokenType.KeywordTrue => (NativeSymbols.Bool, true),
			TokenType.KeywordFalse => (NativeSymbols.Bool, false),
			TokenType.StringLiteral => ParseString(valueSpan),
			TokenType.CharLiteral => ParseChar(valueSpan),
			_ => (type, value)
		};
		
		// TODO We should emit diagnostics here
		type ??= NativeSymbols.Invalid;
		
		return new ResolvedLiteralExpressionNode(type, value, node);
	}
	
	private IResolvedExpressionNode ResolveSuffixedLiteral(LiteralExpressionNode node, string suffix)
	{
		if (suffix == UnsignedSuffix)
			return node.Token.Type == TokenType.IntegerLiteral &&
			       Scanner.TryParseInteger(node.Token.Text, out var value)
				? new ResolvedLiteralExpressionNode(NativeSymbols.UntypedUnsignedInteger, value, node)
				: Error(node, $"Cannot use '{suffix}' on {DescribeLiteralKind(node.Token.Type)} literals",
					CurrentTargetType);
		
		var type = NativeSymbols.Resolve(suffix) as PrimitiveType;
		if (type is not (FloatType or IntegerType))
			return Error(node, $"Invalid suffix '{suffix}'", CurrentTargetType);
		
		return (node.Token.Type, type) switch
		{
			(TokenType.IntegerLiteral, IntegerType integer) => ResolveSuffixedInteger(node, integer),
			(TokenType.IntegerLiteral or TokenType.FloatLiteral, FloatType floating) =>
				ResolveSuffixedFloat(node, floating),
			(TokenType.CharLiteral, IntegerType integer) => ResolveSuffixedChar(node, integer),
			(TokenType.StringLiteral, IntegerType
			{
				Kind: PrimitiveTypeKind.UInt8 or PrimitiveTypeKind.UInt16 or PrimitiveTypeKind.UInt32
			} unit) => ResolveCodeUnits(node, unit),
			(TokenType.FloatLiteral, _) => Error(node, $"Cannot use '{suffix}' on float literals", CurrentTargetType),
			(TokenType.CharLiteral, _) => Error(node, $"Cannot use '{suffix}' on char literals", CurrentTargetType),
			_ => Error(node, $"Cannot use '{suffix}' on string literals", CurrentTargetType)
		};
	}
	
	private IResolvedExpressionNode ResolveSuffixedInteger(LiteralExpressionNode node, IntegerType type)
	{
		var text = type.IsSigned ? ConsumeNegation(node.Token.AsSpan()) : node.Token.Text;
		return Scanner.TryParseInteger(text, out var value) && _evaluator.Fits(value, type)
			? new ResolvedLiteralExpressionNode(type, value, node)
			: Error(node, $"'{text}' doesn't fit in '{type.Name}'", CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveSuffixedFloat(LiteralExpressionNode node, FloatType type)
	{
		var text = ConsumeNegation(node.Token.AsSpan());
		var value = ParseFloatValue(text.Replace("_", ""), type);
		return double.IsFinite(value)
			? new ResolvedLiteralExpressionNode(type, value, node)
			: Error(node, $"'{text}' doesn't fit in '{type.Name}'", CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveSuffixedChar(LiteralExpressionNode node, IntegerType type)
	{
		var (_, code) = ParseChar(node.Token.AsSpan());
		return _evaluator.Fits(code, type)
			? new ResolvedLiteralExpressionNode(type, new BigInteger(code), node)
			: Error(node, $"'{node.Token.Text}' doesn't fit in '{type.Name}'", CurrentTargetType);
	}
	
	private ResolvedArrayExpressionNode ResolveCodeUnits(LiteralExpressionNode node, IntegerType type)
	{
		var text = node.Token.Text;
		IEnumerable<BigInteger> units = type.Kind switch
		{
			PrimitiveTypeKind.UInt8 => Encoding.UTF8.GetBytes(text).Select(static unit => (BigInteger)unit),
			PrimitiveTypeKind.UInt16 => text.Select(static unit => (BigInteger)unit),
			_ => text.EnumerateRunes().Select(static rune => (BigInteger)rune.Value)
		};
		
		ImmutableArray<IResolvedExpressionNode> values =
			[..units.Select(unit => new ResolvedLiteralExpressionNode(type, unit, node))];
		
		return new ResolvedArrayExpressionNode(_typePool.GetArrayType(type, values.Length), values, node);
	}
	
	private (TypeSymbol? Type, object? Value) ParseInteger(ReadOnlySpan<char> span)
	{
		if (Scanner.TryParseInteger(ConsumeNegation(span), out var untypedValue))
			return (NativeSymbols.UntypedInteger, untypedValue);
		
		return (null, null);
	}
	
	private (TypeSymbol? Type, object? Value) ParseFloat(ReadOnlySpan<char> span) =>
		(NativeSymbols.UntypedFloat, ConsumeNegation(span).Replace("_", ""));
	
	private string ConsumeNegation(ReadOnlySpan<char> span)
	{
		if (!_unaryOpJobs.TryPeek(out var job) || job is not { Op: TokenType.OpMinus, Consumed: false })
			return span.ToString();
		
		job.Consumed = true;
		return $"-{span}";
	}
	
	private (TypeSymbol type, uint value) ParseChar(ReadOnlySpan<char> span)
	{
		Rune.DecodeFromUtf16(span, out var rune, out _);
		return (NativeSymbols.Char, (uint)rune.Value);
	}
	
	private (TypeSymbol type, object? value) ParseNull() =>
		(NativeSymbols.UntypedNull, null);
	
	private (TypeSymbol type, string value) ParseString(ReadOnlySpan<char> span) =>
		(NativeSymbols.UntypedString, new string(span));
	
	private IResolvedExpressionNode MaterializeWithPeer(IResolvedExpressionNode operand, TypeSymbol peerType)
	{
		if (peerType is UntypedType)
			return operand;
		
		var materialized = MaterializeExpression(operand, peerType);
		return materialized.Type is UntypedType
			? operand
			: materialized;
	}
	
	private IResolvedExpressionNode MaterializeAsDefault(IResolvedExpressionNode node)
	{
		if (node is ResolvedCaseNameExpressionNode caseName)
			return GetFallback(caseName);
		
		switch (node.Type)
		{
			case UntypedIntegerType when node is ResolvedLiteralExpressionNode literal:
			{
				var targetType = FindDefaultIntegerType(literal);
				return targetType is not null
					? MaterializeInteger(literal, targetType)
					: Error(node.Syntax, "Integer literal too large to fit any type", targetType);
			}
			
			case UntypedIntegerType { IsUnsigned: true }:
				return MaterializeExpression(node, NativeSymbols.UInt32);
			
			case UntypedIntegerType:
				return MaterializeExpression(node, NativeSymbols.Int32);
			
			case UntypedFloatType when node is ResolvedLiteralExpressionNode literal:
				return MaterializeFloat(literal, NativeSymbols.Float64);
			
			case UntypedNullType when node is ResolvedLiteralExpressionNode literal:
				return MaterializeNull(literal, NativeSymbols.VoidPtr);
			
			case UntypedStringType when node is ResolvedLiteralExpressionNode literal:
				return MaterializeStr(literal);
			
			case InterpolatedStringType when node is ResolvedInterpolatedStringExpressionNode interpolation:
				return MaterializeAsDefault(FoldInterpolation(interpolation));
			
			case FunctionGroupType when node is ResolvedFunctionGroupExpressionNode group:
				return MaterializeFunctionAsDefault(group);
			
			case UntypedUndefType:
				return Error(node.Syntax, "Cannot use 'undef' outside variable initializers", null);
			
			default:
				return node;
		}
	}
	
	private IntegerType? FindDefaultIntegerType(ResolvedLiteralExpressionNode literal) => NativeSymbols.PureIntegerTypes
		.Where(type => IntegerValueIn(literal, type) is not null)
		.Select(type => (Type: type,
			Cost: ((UntypedType)literal.Type).MaterializationCost(type, MaterializationMode.Default)))
		.Where(static candidate => candidate.Cost != int.MaxValue)
		.OrderBy(static candidate => candidate.Cost)
		.FirstOrDefault().Type;
	
	private ResolvedLiteralExpressionNode MaterializeLiteral(IExpressionNode syntax, IntegerStorageType type,
		BigInteger value, UntypedType? source = null) => new(type, ConvertInteger(value, type), syntax)
	{
		UntypedSource = source
	};
	
	private static object? ConvertInteger(BigInteger value, IntegerStorageType type) => type.Kind switch
	{
		PrimitiveTypeKind.Int8 => (sbyte)value,
		PrimitiveTypeKind.Int16 => (short)value,
		PrimitiveTypeKind.Int32 => (int)value,
		PrimitiveTypeKind.Int64 => (long)value,
		PrimitiveTypeKind.Int128 => (Int128)value,
		PrimitiveTypeKind.IntSize => value,
		PrimitiveTypeKind.UInt8 => (byte)value,
		PrimitiveTypeKind.UInt16 => (ushort)value,
		PrimitiveTypeKind.UInt32 => (uint)value,
		PrimitiveTypeKind.UInt64 => (ulong)value,
		PrimitiveTypeKind.UInt128 => (UInt128)value,
		PrimitiveTypeKind.UIntSize => value,
		PrimitiveTypeKind.Char => (uint)value,
		_ => throw new InvalidOperationException()
	};
	
	private bool FitsInType(BigInteger value, IntegerType type) => type.Kind switch
	{
		PrimitiveTypeKind.Int8 => value >= sbyte.MinValue && value <= sbyte.MaxValue,
		PrimitiveTypeKind.Int16 => value >= short.MinValue && value <= short.MaxValue,
		PrimitiveTypeKind.Int32 => value >= int.MinValue && value <= int.MaxValue,
		PrimitiveTypeKind.Int64 => value >= long.MinValue && value <= long.MaxValue,
		PrimitiveTypeKind.Int128 => value >= Int128.MinValue && value <= Int128.MaxValue,
		PrimitiveTypeKind.IntSize => value >= _isizeMinValue && value <= _isizeMaxValue,
		PrimitiveTypeKind.UInt8 => value >= byte.MinValue && value <= byte.MaxValue,
		PrimitiveTypeKind.UInt16 => value >= ushort.MinValue && value <= ushort.MaxValue,
		PrimitiveTypeKind.UInt32 => value >= uint.MinValue && value <= uint.MaxValue,
		PrimitiveTypeKind.UInt64 => value >= ulong.MinValue && value <= ulong.MaxValue,
		PrimitiveTypeKind.UInt128 => value >= UInt128.MinValue && value <= UInt128.MaxValue,
		PrimitiveTypeKind.UIntSize => value >= _usizeMinValue && value <= _usizeMaxValue,
		PrimitiveTypeKind.Char => ConstantEvaluator.IsScalarValue(value),
		_ => false
	};
	
	private TypeSymbol? FindLiteralConformer(ResolvedLiteralExpressionNode literal, DynType dyn) =>
		literal.Type is UntypedType untyped
			? NativeSymbols.PrimitiveTypes
				.Where(type => untyped.MaterializationCost(type, MaterializationMode.Overload) != int.MaxValue &&
				               (type is not IntegerType integer || literal.IntegerValue is not { } value ||
				                FitsInType(value, integer)) &&
				               _typePool.Conforms(type, dyn))
				.OrderBy(type => untyped.MaterializationCost(type, MaterializationMode.Overload))
				.FirstOrDefault()
			: null;
	
	private IResolvedExpressionNode MaterializeExpression(IResolvedExpressionNode node, TypeSymbol target)
	{
		RecordMaterialization(node, target);
		if (node is ResolvedCaseNameExpressionNode caseName)
			return ResolveCaseName(caseName, target);
		
		if (node is ResolvedFunctionGroupExpressionNode group)
			return MaterializeFunction(group, target);
		
		if (node.Type is NeverType)
			return new ResolvedConversionExpressionNode(node, new NeverConversion(target), node.Syntax);
		
		if (node is ResolvedUndefExpressionNode { Type: UntypedUndefType })
			return new ResolvedUndefExpressionNode(target, node.Syntax);
		
		if (node is ResolvedMatchExpressionNode { Type: UntypedType } match)
			return new ResolvedMatchExpressionNode(match.Value,
				match.Arms.Select(arm => new ResolvedMatchExpressionArm(arm.Patterns, arm.Guard,
					MaterializeExpression(arm.Value, target))),
				match.IsMut, match.OwnsValue, target, match.Syntax);
		
		if (node is ResolvedInterpolatedStringExpressionNode interpolation)
			return target switch
			{
				FStrType fstr => CreateTemplate(interpolation, fstr),
				StringType => MaterializeExpression(FoldInterpolation(interpolation), target),
				_ => node
			};
		
		if (node is not ResolvedLiteralExpressionNode literal)
			return node;
		
		return node.Type switch
		{
			UntypedIntegerType { IsUnsigned: true } when target is IntegerType { IsSigned: false } t =>
				MaterializeInteger(literal, t),
			UntypedIntegerType { IsUnsigned: true } => literal,
			UntypedIntegerType when target is IntegerType t => MaterializeInteger(literal, t),
			UntypedIntegerType when target is FloatType t => MaterializeIntegerAsFloat(literal, t),
			UntypedFloatType when target is FloatType t => MaterializeFloat(literal, t),
			UntypedNullType when TypePool.TakesNullLiteral(target) => MaterializeNull(literal, target),
			UntypedNullType when target is TypeParameterSymbol { HasNull: true } =>
				MaterializeGenericNull(literal, target),
			UntypedStringType when target is StringType t => t == NativeSymbols.CStr
				? MaterializeCStr(literal)
				: MaterializeStr(literal),
			UntypedStringType when target is FStrType t =>
				new ResolvedFStrExpressionNode(t, [(string)literal.Value!], [], null, literal.Syntax),
			_ => literal
		};
	}
	
	private ResolvedLiteralExpressionNode MaterializeInteger(ResolvedLiteralExpressionNode node, IntegerType target)
	{
		if (node.Type is not UntypedIntegerType)
			return node;
		
		var source = (UntypedType)node.Type;
		if (IntegerValueIn(node, target) is { } fitted)
			return MaterializeLiteral(node.Syntax, target, fitted, source);
		
		var value = (BigInteger)node.Value!;
		var fallback = node.Type is UntypedIntegerType { IsUnsigned: true } ? null : SmallestFittingType(value);
		return fallback is not null
			? MaterializeLiteral(node.Syntax, fallback, value, source)
			: node;
	}
	
	private ResolvedLiteralExpressionNode MaterializeIntegerAsFloat(ResolvedLiteralExpressionNode node,
		FloatType target)
	{
		var value = DefaultIntegerValue(node);
		return IsExactInFloat(value, target)
			? new ResolvedLiteralExpressionNode(target, (double)value, node.Syntax)
			{
				UntypedSource = node.Type as UntypedType
			}
			: MaterializeInteger(node, NativeSymbols.Int32);
	}
	
	private IResolvedExpressionNode MaterializeFloat(ResolvedLiteralExpressionNode node, FloatType target)
	{
		var value = node.Value!;
		var type = FitsInFloat(value, target) ? target : NativeSymbols.Float64;
		return FitsInFloat(value, type)
			? new ResolvedLiteralExpressionNode(type, ParseFloatValue(value, type), node.Syntax)
			{
				UntypedSource = node.Type as UntypedType
			}
			: Error(node.Syntax, "Float literal too large to fit any type", target);
	}
	
	private static double ParseFloatValue(object value, FloatType type) => value switch
	{
		double number => type == NativeSymbols.Float32 ? (float)number : number,
		_ when type == NativeSymbols.Float32 => float.Parse((string)value, CultureInfo.InvariantCulture),
		_ => double.Parse((string)value, CultureInfo.InvariantCulture)
	};
	
	private static bool FitsInFloat(object value, FloatType type) =>
		double.IsFinite(ParseFloatValue(value, type)) || value is double number && !double.IsFinite(number);
	
	private static bool IsExactInFloat(BigInteger value, FloatType type)
	{
		var (significandBits, maxBitLength) = type == NativeSymbols.Float32 ? (24, 128) : (53, 1024);
		var magnitude = BigInteger.Abs(value);
		if (magnitude.IsZero)
			return true;
		
		var significand = magnitude >> (int)BigInteger.TrailingZeroCount(magnitude);
		return significand.GetBitLength() <= significandBits && magnitude.GetBitLength() <= maxBitLength;
	}
	
	private ResolvedLiteralExpressionNode MaterializeGenericNull(ResolvedLiteralExpressionNode node, TypeSymbol target)
	{
		if (CurrentResolutionContext.ContainingFunction?.Symbol is { } owner)
		{
			if (!_typePool.GenericNullChecks.TryGetValue(owner, out var checks))
				_typePool.GenericNullChecks[owner] = checks = [];
			
			checks.Add((target, node.Syntax.SourceLocation));
		}
		
		return MaterializeNull(node, target);
	}
	
	private ResolvedLiteralExpressionNode MaterializeNull(ResolvedLiteralExpressionNode node, TypeSymbol target)
	{
		if (node.Type is not UntypedNullType)
			return node;
		
		return new ResolvedLiteralExpressionNode(target, null, node.Syntax);
	}
	
	private ResolvedLiteralExpressionNode MaterializeStr(ResolvedLiteralExpressionNode node)
	{
		if (node.Type is not UntypedStringType)
			return node;
		
		var text = (string)node.Value!;
		var byteCount = Encoding.UTF8.GetByteCount(text);
		var bytes = new byte[byteCount];
		Encoding.UTF8.GetBytes(text, bytes);
		return new ResolvedLiteralExpressionNode(NativeSymbols.Str, new StrValue((ulong)byteCount, bytes), node.Syntax);
	}
	
	private ResolvedLiteralExpressionNode MaterializeCStr(ResolvedLiteralExpressionNode node)
	{
		if (node.Type is not UntypedStringType)
			return node;
		
		var text = (string)node.Value!;
		var byteCount = Encoding.UTF8.GetByteCount(text);
		var bytes = new byte[byteCount + 1];
		Encoding.UTF8.GetBytes(text, bytes);
		return new ResolvedLiteralExpressionNode(NativeSymbols.CStr, bytes, node.Syntax);
	}
}