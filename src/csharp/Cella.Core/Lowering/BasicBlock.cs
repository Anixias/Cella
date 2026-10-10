using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed class BasicBlock(string label, ControlFlowGraph owner)
{
	public string Label { get; } = label;
	public ControlFlowGraph Owner { get; } = owner;
	public List<IInstruction> Instructions { get; } = [];
	public IBlockTerminator Terminator { get; private set; } = UndefinedTerminator.Instance;
	
	public void SetTerminatorRaw(IBlockTerminator terminator)
		=> Terminator = terminator;
	
	public void SetTerminator(IBlockTerminator terminator) =>
		Owner.SetTerminator(this, terminator);
	
	public void FillTerminator(IBlockTerminator terminator)
	{
		if (Terminator is UndefinedTerminator)
			Owner.SetTerminator(this, terminator);
	}
	
	public bool HasSuccessor() => Owner.HasSuccessor(this);
	public bool HasPredecessor() => Owner.HasPredecessor(this);
	public IReadOnlySet<BasicBlock> GetSuccessors() => Owner.GetSuccessors(this);
	public IReadOnlySet<BasicBlock> GetPredecessors() => Owner.GetPredecessors(this);
	
	public SourceLocation GetSourceLocation() => Instructions
		.Select(static instruction => instruction.SourceLocation)
		.FirstOrDefault(static location => location != SourceLocation.None, Terminator.SourceLocation);
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
public sealed class DefaultValue(TypeSymbol type) : Value(type, true, SourceLocation.None);
public sealed class UndefValue(TypeSymbol type) : Value(type, true, SourceLocation.None);

public sealed class SizeOfValue(TypeSymbol target, SourceLocation sourceLocation)
	: Value(NativeSymbols.UIntSize, false, sourceLocation)
{
	public TypeSymbol Target { get; } = target;
}

public sealed class ValueParameterValue(TypeParameterSymbol parameter, TypeSymbol type, SourceLocation sourceLocation)
	: Value(type, false, sourceLocation)
{
	public TypeParameterSymbol Parameter { get; } = parameter;
}

public sealed class AlignOfValue(TypeSymbol target, SourceLocation sourceLocation)
	: Value(NativeSymbols.UIntSize, false, sourceLocation)
{
	public TypeSymbol Target { get; } = target;
}

public sealed class NewValue(TypeSymbol type, SourceLocation sourceLocation) : Value(type, false, sourceLocation);

public sealed class VariableValue(VariableInfo variable, SourceLocation sourceLocation)
	: Value(variable.Type, false, sourceLocation)
{
	public VariableInfo Variable { get; } = variable;
}

public sealed class GlobalValue(GlobalInfo global, SourceLocation sourceLocation)
	: Value(global.Type, false, sourceLocation)
{
	public GlobalInfo Global { get; } = global;
}

public sealed class MoveValue(Value place) : Value(place.Type, false, place.SourceLocation)
{
	public Value Place { get; } = place;
	public bool IsImplicit { get; init; }
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

public sealed class FunctionReferenceValue(FunctionInfo function, TypeSymbol type, SourceLocation sourceLocation)
	: Value(type, true, sourceLocation)
{
	public FunctionInfo Function { get; } = function;
}

public sealed class ClosureValue
(
	FunctionInfo function,
	IEnumerable<Value> captures,
	TypeSymbol type,
	SourceLocation sourceLocation
) : Value(type, false, sourceLocation)
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<Value> Captures { get; } = captures.ToImmutableArray();
}

public sealed class EnvironmentValue(int index, TypeSymbol type) : Value(type, false, SourceLocation.None)
{
	public int Index { get; } = index;
}

public sealed class OwnClosureValue
(
	FunctionInfo function,
	IEnumerable<Value> captures,
	ClosureType type,
	SourceLocation sourceLocation
) : Value(type, false, sourceLocation)
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<Value> Captures { get; } = captures.ToImmutableArray();
}

public sealed class ClosureFieldValue(int index, IEnumerable<TypeSymbol> fields, TypeSymbol type)
	: Value(type, false, SourceLocation.None)
{
	public int Index { get; } = index;
	public ImmutableArray<TypeSymbol> Fields { get; } = fields.ToImmutableArray();
}

public sealed class IndirectCallValue
(
	Value target,
	IEnumerable<Value> arguments,
	FunctionType functionType,
	SourceLocation sourceLocation
) : Value(functionType.ReturnType, false, sourceLocation)
{
	public Value Target { get; } = target;
	public ImmutableArray<Value> Arguments { get; } = arguments.ToImmutableArray();
	public FunctionType FunctionType { get; } = functionType;
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

public sealed class EnumValue
(
	EnumSymbol type,
	EnumCaseSymbol enumCase,
	IEnumerable<Value> payload,
	SourceLocation sourceLocation
) : Value(type, false, sourceLocation)
{
	public EnumCaseSymbol Case { get; } = enumCase;
	public ImmutableArray<Value> Payload { get; } = payload.ToImmutableArray();
}

public sealed class EnumTagValue(IntegerType type, Value target, SourceLocation sourceLocation)
	: Value(type, false, sourceLocation)
{
	public Value Target { get; } = target;
}

public sealed class EnumPayloadValue
(
	TypeSymbol type,
	Value target,
	EnumCaseSymbol enumCase,
	int index,
	SourceLocation sourceLocation
) : Value(type, false, sourceLocation)
{
	public Value Target { get; } = target;
	public EnumCaseSymbol Case { get; } = enumCase;
	public int Index { get; } = index;
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

public sealed class FStrValue
(
	FStrType type,
	IEnumerable<string> texts,
	Value? text,
	Value? values,
	int holes,
	SourceLocation sourceLocation
) : Value(type, false, sourceLocation)
{
	public ImmutableArray<string> Texts { get; } = texts.ToImmutableArray();
	public Value? Text { get; } = text;
	public Value? Values { get; } = values;
	public int Holes { get; } = holes;
}

public sealed class AtomicValue
(
	AtomicAccess access,
	AtomicOrdering ordering,
	Value? pointer,
	BinaryOperation? operation,
	Value? expected,
	Value? value,
	TypeSymbol type,
	SourceLocation sourceLocation
) : Value(type, false, sourceLocation)
{
	public AtomicAccess Access { get; } = access;
	public AtomicOrdering Ordering { get; } = ordering;
	public Value? Pointer { get; } = pointer;
	public BinaryOperation? Operation { get; } = operation;
	public Value? Expected { get; } = expected;
	public Value? Operand { get; } = value;
}

public sealed class FStrPartValue
(
	TypeSymbol type,
	Value target,
	FStrPart part,
	Value index,
	SourceLocation sourceLocation
) : Value(type, false, sourceLocation)
{
	public Value Target { get; } = target;
	public FStrPart Part { get; } = part;
	public Value Index { get; } = index;
}

#region Operations
public sealed class UnaryOpValue(TypeSymbol type, Value operand, UnaryOperation op, SourceLocation sourceLocation)
	: Value(type, operand.IsConstant, sourceLocation)
{
	public Value Operand { get; } = operand;
	public UnaryOperation Op { get; } = op;
}

public sealed class BinOpValue
(
	TypeSymbol type,
	Value left,
	Value right,
	BinaryOperation op,
	SourceLocation sourceLocation
) : Value(type, left.IsConstant && right.IsConstant, sourceLocation)
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

public sealed class PointerOffsetValue
(
	PointerType type,
	Value pointer,
	Value offset,
	BinaryOperation op,
	SourceLocation sourceLocation
) : Value(type, false, sourceLocation)
{
	public Value Pointer { get; } = pointer;
	public Value Offset { get; } = offset;
	public BinaryOperation Op { get; } = op;
}

public sealed class PointerDifferenceValue
(
	Value left,
	Value right,
	PointerType pointerType,
	SourceLocation sourceLocation
) : Value(NativeSymbols.IntSize, false, sourceLocation)
{
	public Value Left { get; } = left;
	public Value Right { get; } = right;
	public PointerType PointerType { get; } = pointerType;
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

public sealed class ConditionalBranchTerminator
(
	Value condition,
	BasicBlock trueTarget,
	BasicBlock falseTarget,
	SourceLocation sourceLocation = default
) : IBlockTerminator
{
	public Value Condition { get; } = condition;
	public BasicBlock TrueTarget { get; } = trueTarget;
	public BasicBlock FalseTarget { get; } = falseTarget;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}