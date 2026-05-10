using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed class BasicBlock(string label)
{
	public string Label { get; } = label;
	public List<IInstruction> Instructions { get; } = [];
	public IBlockTerminator Terminator { get; set; } = UndefinedTerminator.Instance;
	
	public SourceLocation GetSourceLocation()
	{
		if (Instructions.Count == 0)
			return SourceLocation.None;
		
		var (source, range) = Instructions[0].SourceLocation;
		range = range.Join(Instructions[^1].SourceLocation.Range);
		return new SourceLocation(source, range);
	}
	
	public void FillTerminator(IBlockTerminator terminator)
	{
		if (Terminator is UndefinedTerminator)
			Terminator = terminator;
	}
}

public abstract class Value(TypeSymbol type, SourceLocation sourceLocation)
{
	public TypeSymbol Type { get; } = type;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsConstant { get; protected init; }
	
	protected Value(TypeSymbol type, bool isConstant, SourceLocation sourceLocation) : this(type, sourceLocation)
	{
		IsConstant = isConstant;
	}
}

public sealed class ConstantValue(TypeSymbol type, object? value) : Value(type, true, SourceLocation.None)
{
	public static ConstantValue True { get; } = new(NativeSymbols.Bool, true);
	public static ConstantValue False { get; } = new(NativeSymbols.Bool, false);
	
	public object? Value { get; } = value;
}

// Used to zero-initialize memory
public sealed class ZeroValue(TypeSymbol type) : Value(type, true, SourceLocation.None);
public sealed class UndefValue(TypeSymbol type) : Value(type, true, SourceLocation.None);

public sealed class HeapValue(TypeSymbol type, Value initializer, SourceLocation sourceLocation)
	: Value(type, false, sourceLocation)
{
	public Value Initializer { get; } = initializer;
}

public sealed class VariableValue(VariableInfo variable, SourceLocation sourceLocation)
	: Value(variable.Type, false, sourceLocation)
{
	public VariableInfo Variable { get; } = variable;
}

public sealed class ConversionValue(Value source, Conversion conversion, SourceLocation sourceLocation)
	: Value(conversion.To, source.IsConstant && conversion.IsConstant, sourceLocation)
{
	public Value Source { get; } = source;
	public Conversion Conversion { get; } = conversion;
}

// TODO Detect if the function is constant
public sealed class CallValue(FunctionInfo function, IEnumerable<Value> arguments, SourceLocation sourceLocation)
	: Value(function.Signature.ReturnType, false, sourceLocation)
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<Value> Arguments { get; } = arguments.ToImmutableArray();
}

// TODO Detect if the target and index are constant
public sealed class IndexerValue(TypeSymbol type, Value target, Value index, SourceLocation sourceLocation)
	: Value(type, false, sourceLocation)
{
	public Value Target { get; } = target;
	public Value Index { get; } = index;
}

// TODO Detect if the member is constant
public sealed class AccessValue(TypeSymbol type, Value target, MemberSymbol member, SourceLocation sourceLocation)
	: Value(type, false, sourceLocation)
{
	public Value Target { get; } = target;
	public MemberSymbol Member { get; } = member;
}

public sealed class ArrayValue : Value
{
	public ArrayType ArrayType { get; }
	public ImmutableArray<Value> Elements { get; }
	
	public ArrayValue(ArrayType type, IEnumerable<Value> elements, SourceLocation sourceLocation)
		: base(type, sourceLocation)
	{
		ArrayType = type;
		Elements = elements.ToImmutableArray();
		IsConstant = Elements.All(static e => e.IsConstant);
	}
}

#region Operations
public sealed class UnaryOpValue(TypeSymbol type, Value operand, UnaryOperation op, SourceLocation sourceLocation)
	: Value(type, operand.IsConstant, sourceLocation)
{
	public Value Operand { get; } = operand;
	public UnaryOperation Op { get; } = op;
}

public sealed class BinOpValue(TypeSymbol type, Value left, Value right, BinaryOperation op,
	SourceLocation sourceLocation) : Value(type, left.IsConstant && right.IsConstant, sourceLocation)
{
	public Value Left { get; } = left;
	public Value Right { get; } = right;
	public BinaryOperation Op { get; } = op;
}

public sealed class AssignValue(TypeSymbol type, Value left, Value right, SourceLocation sourceLocation)
	: Value(type, left.IsConstant && right.IsConstant, sourceLocation)
{
	public Value Left { get; } = left;
	public Value Right { get; } = right;
}
#endregion

public interface IBlockTerminator
{
	SourceLocation SourceLocation { get; }
}

public sealed class UndefinedTerminator : IBlockTerminator
{
	public static UndefinedTerminator Instance { get; } = new();
	public SourceLocation SourceLocation => SourceLocation.None;
	
	private UndefinedTerminator()
	{
	}
}

public sealed class ReturnTerminator : IBlockTerminator
{
	public static ReturnTerminator Void { get; } = new(null, SourceLocation.None);
	
	public Value? Value { get; }
	public SourceLocation SourceLocation { get; }
	
	private ReturnTerminator(Value? value, SourceLocation sourceLocation)
	{
		Value = value;
		SourceLocation = sourceLocation;
	}
	
	public static ReturnTerminator FromValue(Value? value, SourceLocation sourceLocation) =>
		value is null ? Void : new(value, sourceLocation);
}

// TODO Note that when emitting code, if the target block is the next block, we can omit the jump instruction
public sealed class BranchTerminator(BasicBlock target, SourceLocation sourceLocation) : IBlockTerminator
{
	public BasicBlock Target { get; } = target;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}

public sealed class ConditionalBranchTerminator(Value condition, BasicBlock trueTarget, BasicBlock falseTarget,
	SourceLocation sourceLocation = default) : IBlockTerminator
{
	public Value Condition { get; } = condition;
	public BasicBlock TrueTarget { get; } = trueTarget;
	public BasicBlock FalseTarget { get; } = falseTarget;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}