using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;

namespace Cella.Core.Analysis;

public sealed class KnownValues : IEquatable<KnownValues>
{
	private readonly (LocalVariableSymbol Symbol, Constant Value)[] _values;
	private readonly int _hash;
	
	private KnownValues((LocalVariableSymbol Symbol, Constant Value)[] values)
	{
		_values = values;
		_hash = values.Aggregate(0, static (sum, entry) => sum + HashCode.Combine(entry.Symbol, HashValue(entry.Value)));
	}
	
	public static KnownValues Empty { get; } = new([]);
	
	public IReadOnlyList<(LocalVariableSymbol Symbol, Constant Value)> Entries => _values;
	
	public Constant? Get(LocalVariableSymbol symbol) => IndexOf(symbol) is var index and >= 0 ? _values[index].Value : null;
	
	public KnownValues With(LocalVariableSymbol symbol, Constant? value)
	{
		var index = IndexOf(symbol);
		if (value is null)
			return index < 0 ? this : new([.._values[..index], .._values[(index + 1)..]]);
		
		if (index < 0)
			return new([.._values, (symbol, value)]);
		
		if (Same(_values[index].Value, value))
			return this;
		
		var values = ((LocalVariableSymbol Symbol, Constant Value)[])_values.Clone();
		values[index] = (symbol, value);
		return new(values);
	}
	
	public KnownValues Intersect(KnownValues other)
	{
		if (ReferenceEquals(this, other))
			return this;
		
		var kept = _values.Where(entry => other.Get(entry.Symbol) is { } value && Same(entry.Value, value)).ToArray();
		return kept.Length == _values.Length ? this : new(kept);
	}
	
	public bool Equals(KnownValues? other) => other is not null &&
	                                          (ReferenceEquals(this, other) ||
	                                           other._values.Length == _values.Length && other._hash == _hash &&
	                                           _values.All(entry => other.Get(entry.Symbol) is { } value &&
	                                                                Same(entry.Value, value)));
	
	public override bool Equals(object? obj) => obj is KnownValues other && Equals(other);
	
	public override int GetHashCode() => _hash;
	
	private int IndexOf(LocalVariableSymbol symbol)
	{
		for (var i = 0; i < _values.Length; i++)
		{
			if (_values[i].Symbol == symbol)
				return i;
		}
		
		return -1;
	}
	
	private static int HashValue(Constant value) => value switch
	{
		IntegerConstant integer => integer.Value.GetHashCode(),
		BoolConstant flag => flag.Value ? 1 : 0,
		_ => 0
	};
	
	internal static bool Same(Constant first, Constant second) => (first, second) switch
	{
		(IntegerConstant a, IntegerConstant b) => a.Value == b.Value && a.Type == b.Type,
		(BoolConstant a, BoolConstant b) => a.Value == b.Value,
		_ => false
	};
}

public sealed class ValueEvaluator(ConstantEvaluator evaluator)
{
	public Constant? Evaluate(Value value, KnownValues known) => value switch
	{
		ConstantValue { Type: IntegerType type } constant when ToInteger(constant.Value) is { } number =>
			new IntegerConstant(type, number),
		ConstantValue { Value: bool flag } => BoolConstant.From(flag),
		VariableValue { Variable.Symbol: LocalVariableSymbol local } => known.Get(local),
		BinOpValue v => EvaluateBinary(v, known),
		UnaryOpValue v => EvaluateUnary(v, known),
		ConversionValue v => Evaluate(v.Source, known) is { } source ? Convert(source, v.Conversion) : null,
		_ => null
	};
	
	public BigInteger? EvaluateInteger(Value value, KnownValues known) =>
		Evaluate(value, known) is IntegerConstant integer ? integer.Value : null;
	
	public IntegerConstant? Advance(IntegerConstant value, BigInteger delta, bool wraps) =>
		value.Type is not IntegerType type ? null
		: wraps ? new IntegerConstant(type, evaluator.Wrap(value.Value + delta, type))
		: evaluator.Fits(value.Value + delta, type) ? new IntegerConstant(type, value.Value + delta)
		: null;
	
	private static BigInteger? ToInteger(object? value) => value switch
	{
		BigInteger number => number,
		sbyte number => number,
		short number => number,
		int number => number,
		long number => number,
		Int128 number => (BigInteger)number,
		byte number => number,
		ushort number => number,
		uint number => number,
		ulong number => number,
		UInt128 number => (BigInteger)number,
		_ => null
	};
	
	private Constant? EvaluateBinary(BinOpValue value, KnownValues known)
	{
		var left = Evaluate(value.Left, known);
		if (value.Op is BinaryOperation.LogicalAnd or BinaryOperation.LogicalOr)
		{
			var deciding = value.Op == BinaryOperation.LogicalOr;
			if (left is BoolConstant decided && decided.Value == deciding)
				return decided;
			
			var right = Evaluate(value.Right, known);
			if (right is BoolConstant { Value: var flag } && flag == deciding)
				return right;
			
			return left is null || right is null ? null : evaluator.Apply(value.Op, left, right, value.Type);
		}
		
		if (left is null || Evaluate(value.Right, known) is not { } other)
			return null;
		
		if (left is IntegerConstant a && other is IntegerConstant b && value.Type is IntegerType type &&
		    value.Op is BinaryOperation.Addition or BinaryOperation.Subtraction or BinaryOperation.Multiplication)
		{
			var exact = value.Op switch
			{
				BinaryOperation.Addition => a.Value + b.Value,
				BinaryOperation.Subtraction => a.Value - b.Value,
				_ => a.Value * b.Value
			};
			
			return evaluator.Fits(exact, type) ? new IntegerConstant(type, exact) : null;
		}
		
		return evaluator.Apply(value.Op, left, other, value.Type);
	}
	
	private Constant? EvaluateUnary(UnaryOpValue value, KnownValues known)
	{
		if (value.Op is UnaryOperation.AddressOf or UnaryOperation.Dereference ||
		    Evaluate(value.Operand, known) is not { } operand)
			return null;
		
		return (value.Op, operand, value.Type) switch
		{
			(UnaryOperation.Identity, _, _) => operand,
			(UnaryOperation.LogicalNot, BoolConstant flag, _) => BoolConstant.From(!flag.Value),
			(UnaryOperation.Negation, IntegerConstant integer, IntegerType type) when evaluator.Fits(-integer.Value, type) =>
				new IntegerConstant(type, -integer.Value),
			(UnaryOperation.BitwiseNot, IntegerConstant integer, IntegerType type) =>
				new IntegerConstant(type, evaluator.Wrap(~integer.Value, type)),
			(UnaryOperation.BitwiseNot, BoolConstant flag, _) => BoolConstant.From(!flag.Value),
			_ => null
		};
	}
	
	private Constant? Convert(Constant source, Conversion conversion) => (conversion, source) switch
	{
		(IdentityConversion, _) => source,
		(IntegerConversion { To: IntegerType to }, IntegerConstant integer) => evaluator.Fits(integer.Value, to)
			? new IntegerConstant(to, integer.Value)
			: null,
		(_, IntegerConstant or BoolConstant) => evaluator.Convert(source, conversion) is var converted and
			(IntegerConstant or BoolConstant)
			? converted
			: null,
		_ => null
	};
}