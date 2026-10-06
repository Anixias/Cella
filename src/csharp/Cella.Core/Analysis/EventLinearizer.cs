using System.Numerics;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;

namespace Cella.Core.Analysis;

public sealed class EventLinearizer
{
	private readonly Dictionary<int, List<LocalVariableSymbol>> _scopeLocals = [];
	private List<MemoryEvent> events = [];
	
	public EventLinearizer(LoweredFunction function)
	{
		foreach (var block in function.Blocks)
		{
			foreach (var instruction in block.Instructions)
			{
				if (instruction is LocalVarInstruction local)
					_scopeLocals.GetOrAdd(local.ScopeId).Add(local.Symbol);
			}
		}
	}
	
	public static Dictionary<BasicBlock, List<MemoryEvent>> Linearize(LoweredFunction function)
	{
		var linearizer = new EventLinearizer(function);
		return function.Blocks.ToDictionary(static b => b, linearizer.Linearize);
	}
	
	private List<MemoryEvent> Linearize(BasicBlock block) =>
		[..block.Instructions.SelectMany(Linearize), ..Linearize(block.Terminator)];
	
	public List<MemoryEvent> Linearize(IInstruction instruction)
	{
		events = [];
		AddInstruction(instruction);
		return events;
	}
	
	public List<MemoryEvent> Linearize(IBlockTerminator terminator)
	{
		events = [];
		switch (terminator)
		{
			case ConditionalBranchTerminator conditional:
				AddValue(conditional.Condition);
				break;
			
			case ReturnTerminator { Value: { } value }:
				AddValue(value);
				break;
		}
		
		return events;
	}
	
	private void AddInstruction(IInstruction instruction)
	{
		switch (instruction)
		{
			case LocalVarInstruction i:
				AddValue(i.Initializer);
				events.Add(new DefineEvent(i.Symbol, i.Initializer switch
				{
					UndefValue => DefineKind.Undef,
					DefaultValue => DefineKind.Default,
					_ => DefineKind.Value
				}, i.SourceLocation));
				
				break;
			
			case ExpressionInstruction i:
				AddValue(i.Value);
				break;
			
			case DropInstruction i:
				AddOperands(i.Value);
				if (GetPlace(i.Value) is { } place)
					events.Add(new DropEvent(place, i));
				
				break;
			
			case EndScopeInstruction i when _scopeLocals.TryGetValue(i.ScopeId, out var locals):
				foreach (var local in locals)
					events.Add(new StorageDeadEvent(local, i.SourceLocation));
				
				break;
		}
	}
	
	private void AddValue(Value value)
	{
		while (true)
		{
			switch (value)
			{
				case MoveValue v:
					AddAccess(v.Place, AccessKind.Move);
					break;
				
				case AssignValue v:
					AddValue(v.Right);
					AddOperands(v.Left);
					if (GetPlace(v.Left) is { } target)
						events.Add(new WriteEvent(target, v.Left.SourceLocation));
					
					break;
				
				case UnaryOpValue { Op: UnaryOperation.AddressOf } v:
					AddAccess(v.Operand, AccessKind.Borrow);
					break;
				
				case AccessValue { Member: not FieldSymbol } v:
					value = v.Target;
					continue;
				
				case VariableValue or GlobalValue or AccessValue or IndexerValue or EnumPayloadValue or
					UnaryOpValue { Op: UnaryOperation.Dereference }:
					AddAccess(value, AccessKind.Read);
					break;
				
				case EnumTagValue v:
					value = v.Target;
					continue;
				
				case CallValue
				{
					Function.Symbol.Kind: FunctionKind.Constructor,
					Arguments: [UnaryOpValue { Op: UnaryOperation.AddressOf, Operand: var self }, ..]
				} v:
					AddValues(v.Arguments.Skip(1));
					AddOperands(self);
					if (GetPlace(self) is { } constructed)
						events.Add(new WriteEvent(constructed, self.SourceLocation));
					
					break;
				
				case CallValue v:
					AddValues(v.Arguments);
					break;
				
				case IndirectCallValue v:
					AddValue(v.Target);
					AddValues(v.Arguments);
					break;
				
				case ConversionValue v:
					value = v.Source;
					continue;
				
				case UnaryOpValue v:
					value = v.Operand;
					continue;
				
				case BinOpValue v:
					AddValue(v.Left);
					value = v.Right;
					continue;
				
				case PointerOffsetValue v:
					AddValue(v.Pointer);
					value = v.Offset;
					continue;
				
				case PointerDifferenceValue v:
					AddValue(v.Left);
					value = v.Right;
					continue;
				
				case EnumValue v:
					AddValues(v.Payload);
					break;
				
				case ArrayValue v:
					AddValues(v.Elements);
					break;
			}
			
			break;
		}
	}
	
	private void AddValues(IEnumerable<Value> values)
	{
		foreach (var value in values)
			AddValue(value);
	}
	
	private void AddAccess(Value place, AccessKind kind)
	{
		AddOperands(place);
		if (GetPlace(place) is { } target)
			events.Add(new AccessEvent(target, kind, place.SourceLocation));
	}
	
	private void AddOperands(Value place)
	{
		switch (place)
		{
			case AccessValue v:
				AddBase(v.Target);
				break;
			
			case IndexerValue v:
				AddBase(v.Target);
				AddValue(v.Index);
				break;
			
			case EnumPayloadValue v:
				AddBase(v.Target);
				break;
			
			case UnaryOpValue { Op: UnaryOperation.Dereference } v when GetPlace(v) is null:
				AddValue(v.Operand);
				break;
		}
	}
	
	private void AddBase(Value target)
	{
		if (IsPlace(target))
			AddOperands(target);
		else
			AddValue(target);
	}
	
	private static bool IsPlace(Value value) => value switch
	{
		VariableValue or GlobalValue or UnaryOpValue { Op: UnaryOperation.Dereference } => true,
		AccessValue v => IsPlace(v.Target),
		IndexerValue v => IsPlace(v.Target),
		EnumPayloadValue v => IsPlace(v.Target),
		_ => false
	};
	
	private static Place? GetPlace(Value value) => value switch
	{
		VariableValue v => new(v.Variable.Symbol, []),
		UnaryOpValue { Op: UnaryOperation.Dereference, Operand: VariableValue { Variable.Symbol: var symbol } } =>
			symbol switch
			{
				ParameterSymbol { Mode: ParameterMode.Mut } => new(symbol, []),
				LocalVariableSymbol { IsBorrowBinding: true } => new(symbol, [new DerefProjection()]),
				_ => null
			},
		AccessValue { Member: FieldSymbol field } v => GetPlace(v.Target)?.Project(new FieldProjection(field)),
		IndexerValue v => GetPlace(v.Target)?.Project(new IndexProjection(GetConstantIndex(v.Index))),
		EnumPayloadValue v => GetPlace(v.Target)?.Project(new PayloadProjection(v.Case, v.Index)),
		_ => null
	};
	
	private static BigInteger? GetConstantIndex(Value index) =>
		index is ConstantValue { Value: BigInteger value } ? value : null;
}