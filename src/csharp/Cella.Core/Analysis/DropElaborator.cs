using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Analysis;

public enum ElementDrop
{
	Keep,
	Remove,
	Mixed
}

public sealed class DropElaborator(TypePool typePool, Action<InitState, MemoryEvent> transfer, MovePaths paths)
{
	private readonly Dictionary<MovePath, LocalVariableSymbol> _flags = [];
	private readonly Dictionary<MovePath, LocalVariableSymbol> _zeroFlags = [];
	private readonly Dictionary<MovePath, LocalVariableSymbol> _elementFlags = [];
	
	public void Elaborate(LoweredFunction function, InitState initialState,
		IReadOnlyDictionary<BasicBlock, InitState> entryStates,
		IReadOnlyDictionary<DropInstruction, (MovePath Path, InitState State)> drops,
		IReadOnlyDictionary<DropInstruction, ElementDrop> elementDrops)
	{
		if (drops.Count == 0 && elementDrops.Count == 0)
			return;
		
		var replacements = drops.ToDictionary(static drop => drop.Key,
			drop => Elaborate(drop.Key, drop.Value.Path, drop.Value.State));
		
		foreach (var (drop, kind) in elementDrops)
		{
			if (kind == ElementDrop.Mixed && ElaborateMixed(drop) is { } mixed)
				replacements[drop] = mixed;
		}
		
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
				else if (instruction is not DropInstruction removed ||
				         elementDrops.GetValueOrDefault(removed) != ElementDrop.Remove)
					instructions.Add(instruction);
				
				if (state is null)
					continue;
				
				instructions.AddRange(Update(state, linearizer.Linearize(instruction)));
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
			.Concat(_elementFlags.Values.Select(static flag =>
				new LocalVarInstruction(flag, new ZeroValue(flag.Type), SourceLocation.None)))
			.ToList();
		
		function.Blocks[0].Instructions.InsertRange(0, initializers);
	}
	
	private List<IInstruction> Elaborate(DropInstruction drop, MovePath path, InitState state)
	{
		var drops = new List<IInstruction>();
		Elaborate(drop.Value, path, state, drop, null, drops);
		return drops;
	}
	
	private void Elaborate(Value value, MovePath path, InitState state, DropInstruction origin, Value? guard,
		List<IInstruction> drops)
	{
		if (!typePool.NeedsDrop(path.Type) || state.IsUninitialized(path))
			return;
		
		if (state.IsWhollyInitialized(path))
		{
			drops.Add(CreateDrop(value, origin, guard));
			return;
		}
		
		if (paths.TracksElements(path))
		{
			var shape = state.GetElements(path);
			ElaborateShape(value, path.Type, shape, origin, guard, drops,
				HasMaybeLeaf(shape) ? ReadElementFlags(path) : null);
			
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
		if (path.Type is ArrayType array && (array.Length.Sign >= 0 || array.LengthParameter is not null))
		{
			ElaborateElements(value, path, array, state, origin, guard, drops);
			return;
		}
		
		foreach (var (part, child) in GetParts(value, path).Reverse())
		{
			if (child is not null)
				Elaborate(part, child, state, origin, guard, drops);
			else if (typePool.NeedsDrop(part.Type) && own.HasFlag(PathState.Initialized))
				drops.Add(CreateDrop(part, origin, own == PathState.Initialized ? guard : And(guard, ReadFlag(path))));
		}
	}
	
	private void ElaborateShape(Value value, TypeSymbol type, Shape shape, DropInstruction origin, Value? guard,
		List<IInstruction> drops, Value? flags)
	{
		if (!typePool.NeedsDrop(type) || Shape.IsUninitialized(shape))
			return;
		
		if (Shape.IsWhollyInitialized(shape))
		{
			drops.Add(CreateDrop(value, origin, guard));
			return;
		}
		
		switch (shape, type)
		{
			case (LeafShape, _) when flags is not null:
				DropFlagged(value, type, flags, origin, guard, drops);
				break;
			
			case (ElementsShape elements, ArrayType array):
				for (var run = elements.Runs.Length - 1; run >= 0; run--)
				{
					var (start, part) = elements.Runs[run];
					var end = elements.GetEnd(run);
					if (Shape.IsUninitialized(part))
						continue;
					
					if (Shape.IsWhollyInitialized(part))
					{
						DropElements(value, array, start, end - start, origin, guard, drops);
						continue;
					}
					
					if (part is LeafShape && flags is not null && FlagType(array.ElementType) == NativeSymbols.Bool)
					{
						drops.Add(new DropElementsInstruction(value, start, end - start, origin.SourceLocation, guard)
						{
							Flags = flags
						});
						
						continue;
					}
					
					for (var index = end - 1; index >= start; index--)
						ElaborateShape(GetElement(value, array, index), array.ElementType, part, origin, guard, drops,
							flags is null ? null : GetElement(flags, (ArrayType)flags.Type, index));
				}
				
				break;
			
			case (FieldsShape fields, RecordSymbol record):
				var members = typePool.GetMembers(record).OfType<FieldSymbol>().ToList();
				for (var i = members.Count - 1; i >= 0; i--)
					ElaborateShape(new AccessValue(typePool.GetTypeOfMember(members[i]), value, members[i],
							value.SourceLocation), typePool.GetTypeOfMember(members[i]), fields.Parts[i], origin, guard,
						drops, null);
				
				break;
		}
	}
	
	private void DropFlagged(Value value, TypeSymbol type, Value flags, DropInstruction origin, Value? guard,
		List<IInstruction> drops)
	{
		if (flags.Type == NativeSymbols.Bool || type is not ArrayType array)
		{
			drops.Add(CreateDrop(value, origin, And(guard, flags)));
			return;
		}
		
		if (FlagType(array.ElementType) == NativeSymbols.Bool)
		{
			drops.Add(new DropElementsInstruction(value, BigInteger.Zero, array.Length, origin.SourceLocation, guard)
			{
				Flags = flags
			});
			
			return;
		}
		
		for (var index = array.Length - 1; index >= 0; index--)
			DropFlagged(GetElement(value, array, index), array.ElementType,
				GetElement(flags, (ArrayType)flags.Type, index),
				origin, guard, drops);
	}
	
	private List<IInstruction>? ElaborateMixed(DropInstruction drop)
	{
		if (EventLinearizer.GetPlace(drop.Value) is not { } place ||
		    paths.FindPrefix(place) is not ({ } array, var depth) || !paths.TracksElements(array) ||
		    GetElementFlags(array, place, depth) is not { } flags)
			return null;
		
		var drops = new List<IInstruction>();
		DropFlagged(drop.Value, drop.Value.Type, flags, drop, drop.Guard, drops);
		return drops;
	}
	
	private static bool HasMaybeLeaf(Shape shape) => shape switch
	{
		LeafShape leaf => leaf.State.HasFlag(PathState.Initialized) && leaf.State != PathState.Initialized,
		FieldsShape fields => fields.Parts.Any(HasMaybeLeaf),
		ElementsShape elements => elements.Runs.Any(static run => HasMaybeLeaf(run.Shape)),
		_ => false
	};
	
	private Value ReadElementFlags(MovePath array)
	{
		if (!_elementFlags.TryGetValue(array, out var flag))
			_elementFlags[array] = flag = new(new(TokenType.Identifier, SourceLocation.None,
				$".elements{_elementFlags.Count}"), FlagType(array.Type), true);
		
		return new VariableValue(new(flag, flag.Type), SourceLocation.None);
	}
	
	private Value? GetElementFlags(MovePath array, Place place, int depth)
	{
		var flags = ReadElementFlags(array);
		var type = array.Type;
		for (var i = depth; i < place.Path.Length; i++)
		{
			if (type is not ArrayType arrayType || place.Path[i] is not IndexProjection projection)
				return null;
			
			var index = projection.Source ?? new ConstantValue(NativeSymbols.UIntSize, projection.Index!.Value);
			type = arrayType.ElementType;
			flags = new IndexerValue(FlagType(type), flags, index, SourceLocation.None);
		}
		
		return flags;
	}
	
	private TypeSymbol FlagType(TypeSymbol type) => type is ArrayType array && ShapeLayout.GetLength(type) is { } length
		? typePool.GetArrayType(FlagType(array.ElementType), length)
		: NativeSymbols.Bool;
	
	private Value Fill(TypeSymbol type, bool value) => type is ArrayType array && value
		? new ArrayValue(array, Enumerable.Range(0, (int)array.Length).Select(_ => Fill(array.ElementType, true)),
			SourceLocation.None)
		: type is ArrayType
			? new ZeroValue(type)
			: Constant(value);
	
	private List<IInstruction> UpdateElementFlags(MemoryEvent memoryEvent, InitState state)
	{
		var updates = new List<IInstruction>();
		var (place, initializes) = memoryEvent switch
		{
			WriteEvent e => (e.Place, true),
			AccessEvent { Kind: AccessKind.Move } e => (e.Place, false),
			AccessEvent { Kind: AccessKind.Address } e => (e.Place.AddressedStorage, true),
			DefineEvent e => (new Place(e.Local, []), true),
			_ => (null, false)
		};
		
		if (place is null || paths.FindPrefix(place) is not ({ } path, var depth))
			return updates;
		
		foreach (var (array, flag) in _elementFlags)
		{
			var flags = new VariableValue(new(flag, flag.Type), SourceLocation.None);
			if (depth == place.Path.Length && path.Index <= array.Index && array.Index < path.End)
			{
				var shape = state.GetElements(array);
				updates.Add(new ExpressionInstruction(new AssignValue(flag.Type, flags,
					Fill(flag.Type, Shape.IsWhollyInitialized(shape)), SourceLocation.None)));
			}
			else if (path == array && depth < place.Path.Length && GetElementFlags(array, place, depth) is { } target)
			{
				updates.Add(new ExpressionInstruction(new AssignValue(target.Type, target,
					Fill(target.Type, initializes),
					SourceLocation.None)));
			}
		}
		
		return updates;
	}
	
	private void ElaborateElements(Value value, MovePath path, ArrayType array, InitState state, DropInstruction origin,
		Value? guard, List<IInstruction> drops)
	{
		var own = state.GetOwnState(path);
		var dropsRest = typePool.NeedsDrop(array.ElementType) && own.HasFlag(PathState.Initialized);
		var restGuard = dropsRest && own != PathState.Initialized ? And(guard, ReadFlag(path)) : guard;
		var children = path.Children
			.Select(static child => (child, (child.Projection as IndexProjection)?.Index))
			.Where(static entry => entry.Index is not null)
			.OrderByDescending(static entry => entry.Index!.Value);
		
		BigInteger? end = array.LengthParameter is null ? array.Length : null;
		foreach (var (child, index) in children)
		{
			if (dropsRest)
				DropElements(value, array, index!.Value + 1, end - index.Value - 1, origin, restGuard, drops);
			
			Elaborate(GetElement(value, array, index!.Value), child, state, origin, guard, drops);
			end = index.Value;
		}
		
		if (dropsRest)
			DropElements(value, array, BigInteger.Zero, end, origin, restGuard, drops);
	}
	
	private static void DropElements(Value value, ArrayType array, BigInteger start, BigInteger? count,
		DropInstruction origin, Value? guard, List<IInstruction> drops)
	{
		if (count is { IsZero: true })
			return;
		
		drops.Add(count is { IsOne: true }
			? CreateDrop(GetElement(value, array, start), origin, guard)
			: new DropElementsInstruction(value, start, count, origin.SourceLocation, guard));
	}
	
	private static IndexerValue GetElement(Value value, ArrayType array, BigInteger index) =>
		new(array.ElementType, value, new ConstantValue(NativeSymbols.UIntSize, index), value.SourceLocation);
	
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
		var updates = new List<IInstruction>();
		foreach (var memoryEvent in events)
		{
			transfer(state, memoryEvent);
			if (_elementFlags.Count > 0)
				updates.AddRange(UpdateElementFlags(memoryEvent, state));
		}
		
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