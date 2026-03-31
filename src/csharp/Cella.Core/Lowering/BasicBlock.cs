using Cella.Core.Symbols;

namespace Cella.Core.Lowering;

public sealed class BasicBlock(string label)
{
	public string Label { get; } = label;
	public List<IInstruction> Instructions { get; } = [];
	public IBlockTerminator Terminator { get; set; } = UndefinedTerminator.Instance;
}

public abstract class Value(TypeSymbol type)
{
	public TypeSymbol Type { get; } = type;
}

public sealed class ConstantValue(TypeSymbol type, object? value) : Value(type)
{
	public object? Value { get; } = value;
}

public sealed class VariableValue(VariableSymbol variable) : Value(variable.Type)
{
	public VariableSymbol Variable { get; } = variable;
}

public sealed class TemporaryValue(TypeSymbol type, int id) : Value(type)
{
	public int Id { get; } = id;
}

#region Constant Operations
public sealed class ConstAddValue(TypeSymbol type, Value left, Value right) : Value(type)
{
	public Value Left { get; } = left;
	public Value Right { get; } = right;
}

public sealed class ConstSubValue(TypeSymbol type, Value left, Value right) : Value(type)
{
	public Value Left { get; } = left;
	public Value Right { get; } = right;
}

public sealed class ConstMulValue(TypeSymbol type, Value left, Value right) : Value(type)
{
	public Value Left { get; } = left;
	public Value Right { get; } = right;
}

public sealed class ConstNegValue(TypeSymbol type, Value operand) : Value(type)
{
	public Value Operand { get; } = operand;
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