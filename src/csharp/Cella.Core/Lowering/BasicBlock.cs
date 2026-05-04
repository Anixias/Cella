using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
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
	public bool IsConstant { get; protected init; }
	
	protected Value(TypeSymbol type, bool isConstant) : this(type)
	{
		IsConstant = isConstant;
	}
}

public sealed class ConstantValue(TypeSymbol type, object? value) : Value(type, true)
{
	public static ConstantValue True { get; } = new(NativeSymbols.Bool, true);
	public static ConstantValue False { get; } = new(NativeSymbols.Bool, true);
	
	public object? Value { get; } = value;
}

// Used to zero-initialize memory
public sealed class ZeroValue(TypeSymbol type) : Value(type, true);
public sealed class UndefValue(TypeSymbol type) : Value(type, true);

public sealed class HeapValue(TypeSymbol type, Value initializer) : Value(type, false)
{
	public Value Initializer { get; } = initializer;
}

public sealed class VariableValue(VariableInfo variable) : Value(variable.Type, false)
{
	public VariableInfo Variable { get; } = variable;
}

public sealed class ConversionValue(Value source, Conversion conversion)
	: Value(conversion.To, source.IsConstant && conversion.IsConstant)
{
	public Value Source { get; } = source;
	public Conversion Conversion { get; } = conversion;
}

// TODO Detect if the function is constant
public sealed class CallValue(FunctionInfo function, IEnumerable<Value> arguments)
	: Value(function.Signature.ReturnType, false)
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<Value> Arguments { get; } = arguments.ToImmutableArray();
}

// TODO Detect if the target and index are constant
public sealed class IndexerValue(TypeSymbol type, Value target, Value index) : Value(type, false)
{
	public Value Target { get; } = target;
	public Value Index { get; } = index;
}

// TODO Detect if the member is constant
public sealed class AccessValue(TypeSymbol type, Value target, MemberSymbol member) : Value(type, false)
{
	public Value Target { get; } = target;
	public MemberSymbol Member { get; } = member;
}

public sealed class ArrayValue : Value
{
	public ArrayType ArrayType { get; }
	public ImmutableArray<Value> Elements { get; }
	
	public ArrayValue(ArrayType type, IEnumerable<Value> elements) : base(type)
	{
		ArrayType = type;
		Elements = elements.ToImmutableArray();
		IsConstant = Elements.All(static e => e.IsConstant);
	}
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