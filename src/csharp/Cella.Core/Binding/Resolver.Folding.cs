using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private ResolvedLiteralExpressionNode? FoldLiteral(UnaryOpExpressionNode node, IResolvedExpressionNode operand)
	{
		if (AsNumberLiteral(operand) is not { } literal)
			return null;
		
		object? value = (node.Op.Type, literal.Value) switch
		{
			(TokenType.OpPlus, var number) => number,
			(TokenType.OpMinus, BigInteger integer) => -integer,
			(TokenType.OpMinus, double number) => -number,
			(TokenType.OpMinus, string text) => text.StartsWith('-') ? text[1..] : $"-{text}",
			(TokenType.OpTilde, BigInteger integer) => ~integer,
			_ => null
		};
		
		return value is null ? null : CreateFold(node, node.Op.Type, literal.Type, value, [literal]);
	}
	
	private ResolvedLiteralExpressionNode? FoldLiterals(IExpressionNode node, TokenType op,
		IResolvedExpressionNode left,
		IResolvedExpressionNode right)
	{
		if (AsNumberLiteral(left) is not { } leftLiteral || AsNumberLiteral(right) is not { } rightLiteral)
			return null;
		
		if (leftLiteral.Value is BigInteger leftInteger && rightLiteral.Value is BigInteger rightInteger)
		{
			var integer = op is TokenType.OpLessLessLess or TokenType.OpGreaterGreaterGreater
				? FoldRotation(op, leftLiteral, rightInteger)
				: FoldIntegers(op, leftInteger, rightInteger);
			
			return integer is { } value
				? CreateFold(node, op,
					leftLiteral.Type is UntypedIntegerType { IsUnsigned: true } ||
					rightLiteral.Type is UntypedIntegerType { IsUnsigned: true }
						? NativeSymbols.UntypedUnsignedInteger
						: NativeSymbols.UntypedInteger, value, [leftLiteral, rightLiteral])
				: null;
		}
		
		return ToDouble(leftLiteral) is { } leftNumber && ToDouble(rightLiteral) is { } rightNumber &&
		       FoldFloats(op, leftNumber, rightNumber) is { } number
			? CreateFold(node, op, NativeSymbols.UntypedFloat, number, [leftLiteral, rightLiteral])
			: null;
	}
	
	private ResolvedLiteralExpressionNode CreateFold(IExpressionNode node, TokenType op, TypeSymbol type, object value,
		ImmutableArray<ResolvedLiteralExpressionNode> operands)
	{
		var folded = new ResolvedLiteralExpressionNode(type, value, node);
		if (type is UntypedIntegerType)
			_literalFolds[folded] = new(op, operands);
		
		survey?.Track(folded, operands.SelectMany(operand => survey.GetTracked(operand)));
		return folded;
	}
	
	private static ResolvedLiteralExpressionNode? AsNumberLiteral(IResolvedExpressionNode node) =>
		node is ResolvedLiteralExpressionNode { Type: UntypedIntegerType or UntypedFloatType } literal ? literal : null;
	
	private BigInteger? FoldIntegers(TokenType op, BigInteger left, BigInteger right) => op switch
	{
		TokenType.OpPlus or TokenType.OpPlusPercent => left + right,
		TokenType.OpMinus or TokenType.OpMinusPercent => left - right,
		TokenType.OpStar or TokenType.OpStarPercent => left * right,
		TokenType.OpSlash when !right.IsZero => BigInteger.Divide(left, right),
		TokenType.OpPercent when !right.IsZero => BigInteger.Remainder(left, right),
		TokenType.OpAmpersand => left & right,
		TokenType.OpBar => left | right,
		TokenType.OpHat => left ^ right,
		TokenType.OpLessLess when IsFoldableShift(right) => left << (int)right,
		TokenType.OpGreaterGreater when IsFoldableShift(right) => left >> (int)right,
		_ => null
	};
	
	private BigInteger? FoldRotation(TokenType op, ResolvedLiteralExpressionNode value, BigInteger amount) =>
		FindDefaultIntegerType(value) is { } type
			? _evaluator.Rotate(DefaultIntegerValue(value), amount, type, op == TokenType.OpLessLessLess)
			: null;
	
	private bool IsFoldableShift(BigInteger amount) =>
		amount.Sign >= 0 && amount < CountBits(NativeSymbols.UInt128);
	
	private static double? FoldFloats(TokenType op, double left, double right) => op switch
	{
		TokenType.OpPlus => left + right,
		TokenType.OpMinus => left - right,
		TokenType.OpStar => left * right,
		TokenType.OpSlash => left / right,
		TokenType.OpPercent => left % right,
		_ => null
	};
	
	private double? ToDouble(ResolvedLiteralExpressionNode literal) => literal.Type switch
	{
		UntypedFloatType => ParseFloatValue(literal.Value!, NativeSymbols.Float64),
		_ when DefaultIntegerValue(literal) is var integer && IsExactInFloat(integer, NativeSymbols.Float64) =>
			(double)integer,
		_ => null
	};
	
	private BigInteger? EvaluateIn(ResolvedLiteralExpressionNode literal, IntegerType type)
	{
		if (!_literalFolds.TryGetValue(literal, out var fold))
			return literal.Value is BigInteger value && FitsInType(value, type) ? value : null;
		
		if (EvaluateIn(fold.Operands[0], type) is not { } left)
			return null;
		
		if (fold.Operands.Length == 1)
			return fold.Op switch
			{
				TokenType.OpMinus => IfFits(-left, type),
				TokenType.OpTilde => _evaluator.Wrap(~left, type),
				_ => left
			};
		
		if (fold.Op is TokenType.OpLessLess or TokenType.OpGreaterGreater)
		{
			if (fold.Operands[1].Value is not BigInteger amount || amount.Sign < 0 || amount >= CountBits(type))
				return null;
			
			return fold.Op == TokenType.OpLessLess ? _evaluator.Wrap(left << (int)amount, type) : left >> (int)amount;
		}
		
		if (fold.Op is TokenType.OpLessLessLess or TokenType.OpGreaterGreaterGreater)
			return _evaluator.Rotate(left, (BigInteger)fold.Operands[1].Value!, type,
				fold.Op == TokenType.OpLessLessLess);
		
		if (EvaluateIn(fold.Operands[1], type) is not { } right)
			return null;
		
		return fold.Op switch
		{
			TokenType.OpPlus => IfFits(left + right, type),
			TokenType.OpMinus => IfFits(left - right, type),
			TokenType.OpStar => IfFits(left * right, type),
			TokenType.OpPlusPercent => _evaluator.Wrap(left + right, type),
			TokenType.OpMinusPercent => _evaluator.Wrap(left - right, type),
			TokenType.OpStarPercent => _evaluator.Wrap(left * right, type),
			TokenType.OpSlash when !right.IsZero => IfFits(BigInteger.Divide(left, right), type),
			TokenType.OpPercent when !right.IsZero => BigInteger.Remainder(left, right),
			TokenType.OpAmpersand => left & right,
			TokenType.OpBar => left | right,
			TokenType.OpHat => left ^ right,
			_ => null
		};
	}
	
	private BigInteger? IfFits(BigInteger value, IntegerType type) => FitsInType(value, type) ? value : null;
	
	private BigInteger? IntegerValueIn(ResolvedLiteralExpressionNode literal, IntegerType type) =>
		EvaluateIn(literal, type) ?? IfFits((BigInteger)literal.Value!, type);
	
	private BigInteger DefaultIntegerValue(ResolvedLiteralExpressionNode literal) =>
		FindDefaultIntegerType(literal) is { } type && IntegerValueIn(literal, type) is { } value
			? value
			: (BigInteger)literal.Value!;
}