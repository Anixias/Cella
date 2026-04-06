using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

namespace Cella.Core.Lowering;

public sealed class BasicBlock(string label)
{
	public string Label { get; } = label;
	public List<IInstruction> Instructions { get; } = [];
	public IBlockTerminator Terminator { get; set; } = UndefinedTerminator.Instance;
}

public abstract class Value(TypeSymbol type, bool isConstant)
{
	public TypeSymbol Type { get; } = type;
	public bool IsConstant { get; } = isConstant;
}

public sealed class ConstantValue(TypeSymbol type, object? value) : Value(type, true)
{
	public object? Value { get; } = value;
}

public sealed class VariableValue(VariableInfo variable) : Value(variable.Type, false)
{
	public VariableInfo Variable { get; } = variable;
}

public sealed class CallValue(FunctionInfo function, IEnumerable<Value> arguments)
	: Value(function.Signature.ReturnType, false)
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<Value> Arguments { get; } = arguments.ToImmutableArray();
}

#region Operations
public sealed class UnaryOpValue(TypeSymbol type, Value operand, UnaryOperation op) : Value(type, operand.IsConstant)
{
	public Value Operand { get; } = operand;
	public UnaryOperation Op { get; } = op;
}

public sealed class BinOpValue(TypeSymbol type, Value left, Value right, BinaryOperation op)
	: Value(type, left.IsConstant && right.IsConstant)
{
	public Value Left { get; } = left;
	public Value Right { get; } = right;
	public BinaryOperation Op { get; } = op;
}

public sealed class AssignValue(TypeSymbol type, Value left, Value right)
	: Value(type, left.IsConstant && right.IsConstant)
{
	public Value Left { get; } = left;
	public Value Right { get; } = right;
}
#endregion

public interface IBlockTerminator;

public sealed class UndefinedTerminator : IBlockTerminator
{
	public static UndefinedTerminator Instance { get; } = new();
	
	private UndefinedTerminator()
	{
	}
}

public sealed class ReturnTerminator : IBlockTerminator
{
	public static ReturnTerminator Void { get; } = new(null);
	
	public Value? Value { get; }
	
	private ReturnTerminator(Value? value)
	{
		Value = value;
	}
	
	public static ReturnTerminator FromValue(Value? value) => value is null ? Void : new(value);
}

// TODO Note that when emitting code, if the target block is the next block, we can omit the jump instruction
public sealed class BranchTerminator(BasicBlock target) : IBlockTerminator
{
	public BasicBlock Target { get; } = target;
}

public sealed class ConditionalBranchTerminator(Value condition, BasicBlock trueTarget, BasicBlock falseTarget)
	: IBlockTerminator
{
	public Value Condition { get; } = condition;
	public BasicBlock TrueTarget { get; } = trueTarget;
	public BasicBlock FalseTarget { get; } = falseTarget;
}