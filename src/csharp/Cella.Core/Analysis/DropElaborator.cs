using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Analysis;

public sealed class DropElaborator(TypePool typePool, Action<InitState, MemoryEvent> transfer)
{
	private readonly Dictionary<MovePath, LocalVariableSymbol> _flags = [];
	private readonly Dictionary<MovePath, LocalVariableSymbol> _zeroFlags = [];
	
	public void Elaborate(LoweredFunction function, InitState initialState,
		IReadOnlyDictionary<BasicBlock, InitState> entryStates,
		IReadOnlyDictionary<DropInstruction, (MovePath Path, InitState State)> drops)
	{
		if (drops.Count == 0)
			return;
		
		var replacements = drops.ToDictionary(static drop => drop.Key,
			drop => Elaborate(drop.Key, drop.Value.Path, drop.Value.State));
		
		var linearizer = new EventLinearizer(function);
		foreach (var (block, state) in entryStates)
			Replay(block, state.Copy(), linearizer);
		
		foreach (var block in function.Blocks)
		{
			var instructions = new List<IInstruction>();
			var state = entryStates.TryGetValue(block, out var entryState) ? entryState.Copy() : null;
			foreach (var instruction in block.Instructions)
			{
				if (instruction is DropInstruction drop && replacements.TryGetValue(drop, out var replacement))
					instructions.AddRange(replacement);
				else
					instructions.Add(instruction);
				
				if (state is null)
					continue;
				
				var updates = Update(state, linearizer.Linearize(instruction));
				if (instruction is not EndScopeInstruction)
					instructions.AddRange(updates);
			}
			
			if (state is not null)
				instructions.AddRange(Update(state, linearizer.Linearize(block.Terminator)));
			
			block.Instructions.Clear();
			block.Instructions.AddRange(instructions);
		}
		
		var initializers = _flags
			.Select(flag => Declare(flag.Value, initialState.GetOwnState(flag.Key) == PathState.Initialized))
			.Concat(_zeroFlags.Select(flag =>
				Declare(flag.Value, initialState.GetOwnState(flag.Key) == PathState.Unassigned)))
			.ToList();
		
		function.Blocks[0].Instructions.InsertRange(0, initializers);
	}
	
	private List<DropInstruction> Elaborate(DropInstruction drop, MovePath path, InitState state)
	{
		var drops = new List<DropInstruction>();
		Elaborate(drop.Value, path, state, drop, null, drops);
		return drops;
	}
	
	private void Elaborate(Value value, MovePath path, InitState state, DropInstruction origin, Value? guard,
		List<DropInstruction> drops)
	{
		if (!typePool.NeedsDrop(path.Type) || state.IsUninitialized(path))
			return;
		
		if (state.IsWhollyInitialized(path))
		{
			drops.Add(CreateDrop(value, origin, guard));
			return;
		}
		
		if (path.Children.Count == 0)
		{
			drops.Add(CreateDrop(value, origin, And(guard, ReadFlag(path))));
			return;
		}
		
		if (path.Type is RecordSymbol { HasDestructor: true } && GetWholeCondition(path, state) is { } whole)
		{
			drops.Add(CreateDrop(value, origin, And(guard, whole)));
			guard = And(guard, new UnaryOpValue(NativeSymbols.Bool, whole, UnaryOperation.LogicalNot,
				SourceLocation.None));
		}
		
		var own = state.GetOwnState(path);
		foreach (var (part, child) in GetParts(value, path).Reverse())
		{
			if (child is not null)
				Elaborate(part, child, state, origin, guard, drops);
			else if (typePool.NeedsDrop(part.Type) && own.HasFlag(PathState.Initialized))
				drops.Add(CreateDrop(part, origin, own == PathState.Initialized ? guard : And(guard, ReadFlag(path))));
		}
	}
	
	private static DropInstruction CreateDrop(Value value, DropInstruction origin, Value? guard) =>
		new(value, origin.SourceLocation, guard, origin.IsReassignment);
	
	private Value? GetWholeCondition(MovePath path, InitState state)
	{
		var flagged = new List<MovePath>();
		return CollectWholeFlags(path, state, flagged)
			? flagged.Select(Value (flaggedPath) => ReadFlag(flaggedPath))
				.Aggregate(static (left, right) => And(left, right))
			: null;
	}
	
	private static bool CollectWholeFlags(MovePath path, InitState state, List<MovePath> flagged)
	{
		if (state.IsWhollyInitialized(path))
			return true;
		
		if (!path.IsComplete)
		{
			var own = state.GetOwnState(path);
			if (!own.HasFlag(PathState.Initialized))
				return false;
			
			if (own != PathState.Initialized)
				flagged.Add(path);
		}
		
		return path.Children.All(child => CollectWholeFlags(child, state, flagged));
	}
	
	private IEnumerable<(Value Part, MovePath? Child)> GetParts(Value value, MovePath path) => path.Type switch
	{
		RecordSymbol record => typePool.GetMembers(record)
			.OfType<FieldSymbol>()
			.Select(field => ((Value)new AccessValue(typePool.GetTypeOfMember(field), value, field,
				value.SourceLocation), path.GetChild(new FieldProjection(field)))),
		ArrayType { Length.Sign: >= 0 } array => Enumerable.Range(0, (int)array.Length)
			.Select(index => ((Value)new IndexerValue(array.ElementType, value,
					new ConstantValue(NativeSymbols.UIntSize, new BigInteger(index)), value.SourceLocation),
				path.GetChild(new IndexProjection(index)))),
		_ => []
	};
	
	private void Replay(BasicBlock block, InitState state, EventLinearizer linearizer)
	{
		foreach (var instruction in block.Instructions)
			Update(state, linearizer.Linearize(instruction));
		
		Update(state, linearizer.Linearize(block.Terminator));
	}
	
	private List<IInstruction> Update(InitState state, List<MemoryEvent> events)
	{
		var flagged = _flags.Keys.ToList();
		var before = flagged.Select(state.GetOwnState).ToList();
		foreach (var memoryEvent in events)
			transfer(state, memoryEvent);
		
		var updates = new List<IInstruction>();
		for (var i = 0; i < flagged.Count; i++)
		{
			var path = flagged[i];
			var current = state.GetOwnState(path);
			if (current == before[i])
				continue;
			
			if (MayChange(before[i], current, PathState.Initialized))
				updates.Add(Assign(_flags[path], current switch
				{
					PathState.Initialized => ConstantValue.True,
					_ when !current.HasFlag(PathState.Initialized) => ConstantValue.False,
					_ => new BinOpValue(NativeSymbols.Bool, ReadFlag(path), Read(GetZeroFlag(path)),
						BinaryOperation.LogicalOr, SourceLocation.None)
				}));
			
			if (_zeroFlags.TryGetValue(path, out var zeroFlag) && MayChange(before[i], current, PathState.Unassigned))
				updates.Add(Assign(zeroFlag, Constant(current == PathState.Unassigned)));
		}
		
		return updates;
	}
	
	private static bool MayChange(PathState before, PathState after, PathState state) =>
		before.HasFlag(state) || after.HasFlag(state);
	
	private VariableValue ReadFlag(MovePath path)
	{
		if (!_flags.TryGetValue(path, out var flag))
			_flags[path] = flag = CreateFlag($".flag{_flags.Count}");
		
		return Read(flag);
	}
	
	private LocalVariableSymbol GetZeroFlag(MovePath path)
	{
		if (!_zeroFlags.TryGetValue(path, out var flag))
			_zeroFlags[path] = flag = CreateFlag($".zero{_zeroFlags.Count}");
		
		return flag;
	}
	
	private static LocalVariableSymbol CreateFlag(string name) =>
		new(new(TokenType.Identifier, SourceLocation.None, name), NativeSymbols.Bool, true);
	
	private static VariableValue Read(LocalVariableSymbol flag) =>
		new(new(flag, NativeSymbols.Bool), SourceLocation.None);
	
	private static ExpressionInstruction Assign(LocalVariableSymbol flag, Value value) =>
		new(new AssignValue(NativeSymbols.Bool, Read(flag), value, SourceLocation.None));
	
	private static LocalVarInstruction Declare(LocalVariableSymbol flag, bool value) =>
		new(flag, Constant(value), SourceLocation.None);
	
	private static ConstantValue Constant(bool value) => value ? ConstantValue.True : ConstantValue.False;
	
	private static Value And(Value? left, Value right) => left is null
		? right
		: new BinOpValue(NativeSymbols.Bool, left, right, BinaryOperation.LogicalAnd, SourceLocation.None);
}