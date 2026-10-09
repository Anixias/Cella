using Cella.Core.Text;

namespace Cella.Core.Binding.Operations;

public static class OperationMapping
{
	public static BinaryOperation ToBinaryOperation(TokenType op) => op switch
	{
		TokenType.OpPlus or TokenType.OpPlusEqual => BinaryOperation.Addition,
		TokenType.OpMinus or TokenType.OpMinusEqual => BinaryOperation.Subtraction,
		TokenType.OpStar or TokenType.OpStarEqual => BinaryOperation.Multiplication,
		TokenType.OpSlash or TokenType.OpSlashEqual => BinaryOperation.Division,
		TokenType.OpPercent or TokenType.OpPercentEqual => BinaryOperation.Modulo,
		TokenType.OpPlusPercent or TokenType.OpPlusPercentEqual => BinaryOperation.WrappingAddition,
		TokenType.OpMinusPercent or TokenType.OpMinusPercentEqual => BinaryOperation.WrappingSubtraction,
		TokenType.OpStarPercent or TokenType.OpStarPercentEqual => BinaryOperation.WrappingMultiplication,
		TokenType.OpEqualEqual => BinaryOperation.Equal,
		TokenType.OpBangEqual => BinaryOperation.NotEqual,
		TokenType.OpGreater => BinaryOperation.Greater,
		TokenType.OpGreaterEqual => BinaryOperation.GreaterEqual,
		TokenType.OpLess => BinaryOperation.Less,
		TokenType.OpLessEqual => BinaryOperation.LessEqual,
		TokenType.OpAmpersand or TokenType.OpAmpersandEqual => BinaryOperation.BitwiseAnd,
		TokenType.OpBar or TokenType.OpBarEqual => BinaryOperation.BitwiseOr,
		TokenType.OpHat or TokenType.OpHatEqual => BinaryOperation.BitwiseXor,
		TokenType.OpLessLess or TokenType.OpLessLessEqual => BinaryOperation.ShiftLeft,
		TokenType.OpGreaterGreater or TokenType.OpGreaterGreaterEqual => BinaryOperation.ShiftRight,
		TokenType.OpLessLessLess or TokenType.OpLessLessLessEqual => BinaryOperation.RotateLeft,
		TokenType.OpGreaterGreaterGreater or TokenType.OpGreaterGreaterGreaterEqual => BinaryOperation.RotateRight,
		TokenType.OpAmpersandAmpersand => BinaryOperation.LogicalAnd,
		TokenType.OpBarBar => BinaryOperation.LogicalOr,
		_ => throw new InvalidOperationException()
	};
	
	public static UnaryOperation ToUnaryOperation(TokenType op) => op switch
	{
		TokenType.OpPlus => UnaryOperation.Identity,
		TokenType.OpMinus => UnaryOperation.Negation,
		TokenType.OpTilde => UnaryOperation.BitwiseNot,
		TokenType.OpBang => UnaryOperation.LogicalNot,
		TokenType.OpAt => UnaryOperation.AddressOf,
		TokenType.OpStar => UnaryOperation.Dereference,
		_ => throw new InvalidOperationException()
	};
}