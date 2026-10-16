using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Analysis;

public sealed class MemoryChecker(TypePool typePool, ConstantEvaluator evaluator, DiagnosticList diagnostics)
{
	private readonly record struct Uninitialized(string Condition, IEnumerable<MoveSite> Moves);
	
	private readonly record struct Visit(BasicBlock Block, InitState State, List<MemoryEvent> Events);
	
	private sealed record Analysis
	(
		MovePaths Paths,
		InitState EntryState,
		Dictionary<BasicBlock, InitState> EntryStates,
		Dictionary<DropInstruction, (MovePath Path, InitState State)> Drops,
		Dictionary<DropInstruction, ElementDrop> ElementDrops,
		List<Diagnostic> Diagnostics,
		HashSet<VariableSymbol> ErrorRoots
	);
	
	private readonly HashSet<(SourceLocation, string)> _reported = [];
	private readonly Dictionary<FunctionSymbol, BorrowChecker.ClosureSummary> _closureSummaries = [];
	private readonly ShapeLayout _layout = new(typePool);
	private readonly ValueEvaluator _values = new(evaluator);
	
	private ParameterSymbol? destructorSelf;
	private ParameterSymbol? ownSelf;
	private List<Diagnostic> pending = [];
	private HashSet<(SourceLocation, string)> pendingKeys = [];
	private HashSet<VariableSymbol> errorRoots = [];
	
	public void Check(LoweredFunction function)
	{
		destructorSelf = function.Info.Symbol is { Kind: FunctionKind.Destructor, Parameters: [var self] }
			? self
			: null;
		
		ownSelf = function.Info.Symbol is
		{
			Kind: FunctionKind.Method, Syntax: FunctionNode { Receiver: not null },
			Parameters: [{ Mode: ParameterMode.Own } owned, ..]
		}
			? owned
			: null;
		
		AddDestructorExitDrops(function);
		var events = EventLinearizer.Linearize(function);
		var allEvents = events.Values.SelectMany(static e => e).ToList();
		var requiredFields = GetRequiredFields(function);
		var paths = MovePaths.Build(function, allEvents, requiredFields, typePool);
		var entryState = CreateEntryState(function, paths);
		var analysis = Inspect(function, paths, requiredFields, entryState,
			SolveInitialization(function, events, paths, entryState)
				.Select(entry => new Visit(entry.Key, entry.Value, events[entry.Key])));
		
		if (analysis.Diagnostics.Count > 0)
			analysis = Follow(function, events, allEvents, requiredFields, analysis);
		
		foreach (var diagnostic in analysis.Diagnostics)
		{
			if (_reported.Add((diagnostic.SourceLocation, diagnostic.Message)))
				diagnostics.Add(diagnostic);
		}
		
		new BorrowChecker(typePool, diagnostics, _closureSummaries).Check(function, events);
		new DropElaborator(typePool, (state, memoryEvent) => Transfer(state, memoryEvent, analysis.Paths),
				analysis.Paths)
			.Elaborate(function, analysis.EntryState, analysis.EntryStates, analysis.Drops, analysis.ElementDrops);
	}
	
	private Analysis Follow(LoweredFunction function, Dictionary<BasicBlock, List<MemoryEvent>> events,
		List<MemoryEvent> allEvents, List<Place> requiredFields, Analysis fallback)
	{
		var tracked = FindTrackedLocals(function);
		if (tracked.Count == 0)
			return fallback;
		
		var paths = MovePaths.Build(function, allEvents, requiredFields, typePool, true);
		var order = CfgUtils.GetReversePostorder(function);
		var followed = SelectLoops(LoopForest.Find(order), events, fallback.ErrorRoots, tracked);
		var entryState = CreateEntryState(function, paths);
		while (true)
		{
			var follower = new LoopFollower(function, paths, _layout, _values, tracked,
				(state, memoryEvent) => Transfer(state, memoryEvent, paths));
			
			if (follower.Solve(order, followed, entryState) is { } visits)
			{
				return Inspect(function, paths, requiredFields, entryState, visits.Select(visit =>
				{
					var known = visit.Known;
					return new Visit(visit.Block, visit.State, follower.Walk(visit.Block, ref known));
				}));
			}
			
			followed.Remove(follower.Unfollowable!);
		}
	}
	
	private static HashSet<LocalVariableSymbol> FindTrackedLocals(LoweredFunction function)
	{
		var linearizer = new EventLinearizer(function);
		var candidates = new HashSet<LocalVariableSymbol>();
		var excluded = new HashSet<LocalVariableSymbol>();
		foreach (var block in function.Blocks)
		{
			foreach (var instruction in block.Instructions)
				Classify(linearizer.Linearize(instruction), instruction);
			
			Classify(linearizer.Linearize(block.Terminator), null);
		}
		
		candidates.ExceptWith(excluded);
		return candidates;
		
		void Classify(List<MemoryEvent> instructionEvents, IInstruction? instruction)
		{
			foreach (var memoryEvent in instructionEvents)
			{
				switch (memoryEvent)
				{
					case DefineEvent { Local: { Captured: null, IsBorrowBinding: false } local }
						when local.Type is IntegerType || local.Type == NativeSymbols.Bool:
						candidates.Add(local);
						break;
					
					case AccessEvent
					{
						Kind: AccessKind.Borrow or AccessKind.Address, Place.Root: LocalVariableSymbol local
					}:
						excluded.Add(local);
						break;
					
					case WriteEvent { Place: { Root: LocalVariableSymbol local, Path.IsEmpty: true } }
						when !IsTopLevelAssignment(instruction, local):
						excluded.Add(local);
						break;
				}
			}
		}
	}
	
	private static bool IsTopLevelAssignment(IInstruction? instruction, LocalVariableSymbol local) =>
		instruction is ExpressionInstruction
		{
			Value: AssignValue { Left: VariableValue { Variable.Symbol: var target } }
		} &&
		target == local;
	
	private static HashSet<Loop> SelectLoops(List<Loop> loops, Dictionary<BasicBlock, List<MemoryEvent>> events,
		HashSet<VariableSymbol> roots, HashSet<LocalVariableSymbol> tracked)
	{
		var candidates = loops.Where(static loop => loop.HasExit).ToList();
		var selected = candidates.Where(loop => GetBodyEvents(loop, events).Any(e => GetRoot(e) is { } root &&
			roots.Contains(root))).ToHashSet();
		
		var changed = true;
		while (changed)
		{
			var reads = selected.SelectMany(loop => GetBodyEvents(loop, events))
				.OfType<AccessEvent>()
				.Select(static access => access.Place.Root)
				.OfType<LocalVariableSymbol>()
				.Where(tracked.Contains)
				.ToHashSet();
			
			var added = candidates.Where(loop => !selected.Contains(loop) && GetBodyEvents(loop, events).Any(e =>
				e is WriteEvent { Place.Root: LocalVariableSymbol written } && reads.Contains(written))).ToList();
			
			selected.UnionWith(added);
			changed = added.Count > 0;
		}
		
		return selected;
	}
	
	private static IEnumerable<MemoryEvent>
		GetBodyEvents(Loop loop, Dictionary<BasicBlock, List<MemoryEvent>> events) =>
		loop.Body.SelectMany(block => events[block]);
	
	private static VariableSymbol? GetRoot(MemoryEvent memoryEvent) => memoryEvent switch
	{
		AccessEvent e => e.Place.Root,
		WriteEvent e => e.Place.Root,
		DropEvent e => e.Place.Root,
		DefineEvent e => e.Local,
		_ => null
	};
	
	private Analysis Inspect(LoweredFunction function, MovePaths paths, List<Place> requiredFields,
		InitState entryState, IEnumerable<Visit> visits)
	{
		pending = [];
		pendingKeys = [];
		errorRoots = [];
		var reportedMoves = new HashSet<SourceLocation>();
		var drops = new Dictionary<DropInstruction, (MovePath Path, InitState State)>();
		var elementDrops = new Dictionary<DropInstruction, ElementDrop>();
		var entryStates = new Dictionary<BasicBlock, InitState>();
		var flagged = new HashSet<MovePath>();
		var partWrites = new Dictionary<MovePath, SourceLocation>();
		var mixedArrays = new Dictionary<DropInstruction, MovePath?>();
		InitState? returnState = null;
		foreach (var (block, blockEntryState, blockEvents) in visits)
		{
			if (entryStates.TryGetValue(block, out var joinedEntry))
				joinedEntry.JoinWith(blockEntryState);
			else
				entryStates[block] = blockEntryState.Copy();
			
			var state = blockEntryState.Copy();
			foreach (var memoryEvent in blockEvents)
			{
				if (memoryEvent is WriteEvent or AccessEvent { Kind: AccessKind.Move } &&
				    GetEventPlace(memoryEvent) is { } changed &&
				    FindElement(changed, paths) is var (changedArray, at) &&
				    !IsElementLevel(changedArray.Type, changed, at))
					partWrites.TryAdd(changedArray, memoryEvent.Location);
				
				switch (memoryEvent)
				{
					case AccessEvent access:
						CheckAccess(access, state, paths, reportedMoves);
						break;
					
					case WriteEvent write when write.Place.Path.Any(IsComputedIndex):
						ApplyRequiredDefaults(state, write.Place, paths);
						if (FindUninitialized(write.Place, state, paths) is { } uninitialized)
							Report(write.Location,
								$"Cannot assign elements of {uninitialized.Condition} arrays at computed indices",
								DescribeMoves(write.Location, uninitialized.Moves), write.Place.Root);
						
						break;
					
					case WriteEvent { Place.Path.IsEmpty: true } write when write.Place.Root == destructorSelf:
						Report(write.Location, "Cannot reassign 'self' in destructors", [], write.Place.Root);
						break;
					
					case WriteEvent write:
						CheckDeferredWrite(write, state, paths);
						break;
					
					case StorageDeadEvent { Local: { IsLoopBinding: true, IsMutBinding: true } binding }:
						CheckLoopRefill(binding, state, paths, reportedMoves);
						break;
					
					case DropEvent drop when paths.Find(drop.Place) is { } path:
						if (drops.TryGetValue(drop.Instruction, out var recorded))
							recorded.State.JoinWith(state);
						else
							drops[drop.Instruction] = (path, state.Copy());
						
						break;
					
					case DropEvent drop when FindElement(drop.Place, paths) is var (array, depth):
						var kind = ClassifyElementDrop(drop.Place, array, depth, state);
						elementDrops[drop.Instruction] =
							elementDrops.TryGetValue(drop.Instruction, out var previous) && previous != kind
								? ElementDrop.Mixed
								: kind;
						
						mixedArrays[drop.Instruction] = IsElementLevel(array.Type, drop.Place, depth) ? array : null;
						break;
				}
				
				Transfer(state, memoryEvent, paths);
			}
			
			if (block.Terminator is not ReturnTerminator)
				continue;
			
			CheckRefills(function, state, paths, reportedMoves);
			if (returnState is null)
				returnState = state;
			else
				returnState.JoinWith(state);
		}
		
		if (returnState is not null)
			CheckRequiredFields(function, requiredFields, returnState, paths);
		
		foreach (var (drop, (path, state)) in drops)
			CollectFlaggedArrays(path, state, paths, flagged, drop);
		
		foreach (var (drop, kind) in elementDrops)
		{
			if (kind != ElementDrop.Mixed)
				continue;
			
			if (mixedArrays[drop] is { } array)
				flagged.Add(array);
			else
				Report(drop.SourceLocation, "Cannot drop partly initialized elements", [], null);
		}
		
		foreach (var array in flagged)
		{
			if (partWrites.TryGetValue(array, out var location))
				Report(location, "Cannot drop partly initialized elements", [], null);
		}
		
		return new Analysis(paths, entryState, entryStates, drops, elementDrops, pending, errorRoots);
	}
	
	private ElementDrop ClassifyElementDrop(Place place, MovePath array, int depth, InitState state)
	{
		if (place.Path[depth] is IndexProjection { Index: null })
			return ElementDrop.Keep;
		
		var shape = _layout.Get(state.GetElements(array), array.Type, place.Path.AsSpan()[depth..], out _);
		return Shape.IsWhollyInitialized(shape) ? ElementDrop.Keep
			: Shape.IsUninitialized(shape) ? ElementDrop.Remove
			: ElementDrop.Mixed;
	}
	
	private void CollectFlaggedArrays(MovePath path, InitState state, MovePaths paths, HashSet<MovePath> flagged,
		DropInstruction drop)
	{
		if (paths.TracksElements(path) && typePool.NeedsDrop(path.Type) && HasMaybeLeaf(state.GetElements(path)))
		{
			if (HasMaybePart(state.GetElements(path)))
				Report(drop.SourceLocation, "Cannot drop partly initialized elements", [], null);
			else
				flagged.Add(path);
		}
		
		foreach (var child in path.Children)
			CollectFlaggedArrays(child, state, paths, flagged, drop);
	}
	
	private static bool HasMaybeLeaf(Shape shape) => shape switch
	{
		LeafShape leaf => leaf.State.HasFlag(PathState.Initialized) && leaf.State != PathState.Initialized,
		FieldsShape fields => fields.Parts.Any(HasMaybeLeaf),
		ElementsShape elements => elements.Runs.Any(static run => HasMaybeLeaf(run.Shape)),
		_ => false
	};
	
	private static bool HasMaybePart(Shape shape) => shape switch
	{
		FieldsShape fields => fields.Parts.Any(HasMaybeLeaf),
		ElementsShape elements => elements.Runs.Any(static run => HasMaybePart(run.Shape)),
		_ => false
	};
	
	private static bool IsElementLevel(TypeSymbol type, Place place, int depth)
	{
		for (var i = depth; i < place.Path.Length; i++)
		{
			if (type is not ArrayType array || place.Path[i] is not IndexProjection)
				return false;
			
			type = array.ElementType;
		}
		
		return true;
	}
	
	private static Place? GetEventPlace(MemoryEvent memoryEvent) => memoryEvent switch
	{
		WriteEvent e => e.Place,
		AccessEvent e => e.Place,
		_ => null
	};
	
	private static (MovePath Array, int Depth)? FindElement(Place place, MovePaths paths) =>
		paths.FindPrefix(place) is ({ } path, var depth) && depth < place.Path.Length && paths.TracksElements(path) &&
		place.Path[depth] is IndexProjection
			? (path, depth)
			: null;
	
	private void AddDestructorExitDrops(LoweredFunction function)
	{
		if (function.Info.Symbol is not { Kind: FunctionKind.Destructor, Parameters: [var self] } ||
		    function.Info.Signature.GetDeclaredType(0) is not RecordSymbol record)
			return;
		
		var movedFields = EventLinearizer.Linearize(function).Values
			.SelectMany(static events => events)
			.OfType<AccessEvent>()
			.Where(access => access is { Kind: AccessKind.Move, Place.Path: [FieldProjection, ..] } &&
			                 access.Place.Root == self)
			.Select(static access => ((FieldProjection)access.Place.Path[0]).Field)
			.ToHashSet();
		
		var declared = typePool.GetMembers(record).OfType<FieldSymbol>().ToList();
		var fields = declared.SkipWhile(field => !movedFields.Contains(field)).Reverse().ToList();
		typePool.SetDestructorMoves(record, fields.ToHashSet());
		var selfValue = new VariableValue(new(self, function.Info.Signature.ParameterTypes[0]), SourceLocation.None);
		var target = new UnaryOpValue(record, selfValue, UnaryOperation.Dereference, SourceLocation.None);
		foreach (var block in function.Blocks.Where(static block => block.Terminator is ReturnTerminator))
		{
			block.Instructions.AddRange(fields.Select(field => new DropInstruction(
				new AccessValue(typePool.GetTypeOfMember(field), target, field, SourceLocation.None),
				SourceLocation.None)));
		}
	}
	
	private List<Place> GetRequiredFields(LoweredFunction function)
	{
		if (function.Info.Symbol is not { Kind: FunctionKind.Constructor, Parameters: [var self, ..] } ||
		    function.Info.Signature.GetDeclaredType(0) is not RecordSymbol record)
			return [];
		
		return typePool.GetMembers(record)
			.OfType<FieldSymbol>()
			.Where(field => !typePool.HasDefault(field))
			.Select(field => new Place(self, [new FieldProjection(field)]))
			.ToList();
	}
	
	private void CheckRequiredFields(LoweredFunction function, List<Place> requiredFields, InitState state,
		MovePaths paths)
	{
		if (function.Info.Symbol.Syntax is not ConstructorNode constructor)
			return;
		
		var hints = new List<string>();
		foreach (var field in requiredFields)
		{
			if (paths.Find(field) is not { } path || !state.GetUninitialized(path).HasFlag(PathState.Unassigned))
				continue;
			
			var name = ((FieldProjection)field.Path[0]).Field.Name;
			var condition = state.HasInitializedPart(path) ? "partly initialized" : "uninitialized";
			hints.Add($"'{name}' is {condition}");
		}
		
		if (hints.Count > 0)
			Report(constructor.Keyword.SourceLocation, "Cannot leave fields uninitialized", hints,
				function.Info.Symbol.Parameters[0]);
	}
	
	private void CheckAccess(AccessEvent access, InitState state, MovePaths paths,
		HashSet<SourceLocation> reportedMoves)
	{
		if (access.Kind == AccessKind.Move && GetMoveError(access.Place, paths) is { } error)
		{
			Report(access.Location, error, [], access.Place.Root);
			return;
		}
		
		if (access.Kind == AccessKind.Address && FindFillable(access.Place, state, paths) is not null)
			return;
		
		if (FindUninitialized(access.Place, state, paths) is { } uninitialized)
			Report(access.Location, $"Cannot use {uninitialized.Condition} values",
				DescribeMoves(access.Location, uninitialized.Moves), access.Place.Root);
		else if (access.IsImplicit && reportedMoves.Add(access.Location))
			Report(access.Location, $"Cannot move '{access.Type.Name}' values implicitly", [], access.Place.Root);
	}
	
	private static MovePath? FindFillable(Place place, InitState state, MovePaths paths) =>
		place.Root is not LocalVariableSymbol { IsDeferred: true } &&
		paths.FindPrefix(place.AddressedStorage).Path is { } storage &&
		(state.GetUninitialized(storage) & ~PathState.Undef) == PathState.None
			? storage
			: null;
	
	private static bool IsDeferredRoot(Place place) =>
		place is { Root: LocalVariableSymbol { IsDeferred: true }, Path.IsEmpty: true };
	
	private void CheckDeferredWrite(WriteEvent write, InitState state, MovePaths paths)
	{
		if (!IsDeferredRoot(write.Place))
			return;
		
		var path = paths.GetRoot(write.Place.Root);
		if ((state.GetOwnState(path) & (PathState.Initialized | PathState.Moved)) != 0)
			Report(write.Location, "Cannot reassign values",
				DescribeSites(write.Location, state.GetOwnWrites(path), "Assigned"), write.Place.Root);
	}
	
	private Uninitialized? FindUninitialized(Place place, InitState state, MovePaths paths)
	{
		if (paths.FindPrefix(place) is not ({ } path, var depth))
			return null;
		
		if (FindElement(place, paths) is var (array, elementDepth) && !IsComputedIndex(place.Path[elementDepth]))
			return FindUninitializedElement(place, array, elementDepth, state);
		
		var isWhole = depth == place.Path.Length || IsComputedIndex(place.Path[depth]);
		if (isWhole ? state.IsWhollyInitialized(path) : state.GetOwnState(path) == PathState.Initialized)
			return null;
		
		var uninitialized = isWhole ? state.GetUninitialized(path) : state.GetOwnState(path) & ~PathState.Initialized;
		var isPartial = isWhole && state.HasInitializedPart(path);
		if (!uninitialized.HasFlag(PathState.Moved))
			return new(isPartial ? "partly initialized" : "uninitialized", []);
		
		var moves = (isWhole ? state.GetMoves(path) : state.GetOwnMoves(path)).ToList();
		isPartial |= isWhole && moves.Count > 0 && moves.All(move => move.Path != path || move.Depth > 0);
		return new(isPartial ? "partly moved" : "moved", moves);
	}
	
	private Uninitialized? FindUninitializedElement(Place place, MovePath array, int depth, InitState state)
	{
		var shape = _layout.Get(state.GetElements(array), array.Type, place.Path.AsSpan()[depth..], out var reached);
		if (Shape.IsWhollyInitialized(shape))
			return null;
		
		var uninitialized = Shape.Summarize(shape) & ~PathState.Initialized;
		var isPartial = Shape.HasInitializedPart(shape);
		if (!uninitialized.HasFlag(PathState.Moved))
			return new(isPartial ? "partly initialized" : "uninitialized", []);
		
		var moves = Shape.GetMoves(shape).ToList();
		isPartial |= moves.Count > 0 && moves.All(move => move.Depth > reached);
		return new(isPartial ? "partly moved" : "moved", moves);
	}
	
	private static bool IsComputedIndex(Projection projection) => projection is IndexProjection { Index: null };
	
	private void CheckRefills(LoweredFunction function, InitState state, MovePaths paths,
		HashSet<SourceLocation> reportedMoves)
	{
		var symbol = function.Info.Symbol;
		var receiver = symbol.Kind is FunctionKind.Constructor or FunctionKind.Destructor or FunctionKind.Method
			? symbol.Parameters[0]
			: null;
		
		foreach (var parameter in symbol.Parameters)
		{
			if (parameter.Mode != ParameterMode.Mut)
				continue;
			
			var root = paths.GetRoot(parameter);
			var subject = parameter == receiver ? "'self'" : "'mut' parameters";
			foreach (var move in state.GetMoves(root))
			{
				if (parameter == destructorSelf)
					continue;
				
				if (reportedMoves.Add(move.Location))
					Report(move.Location, move.Path == root && move.Depth == 0
						? $"Cannot leave {subject} moved"
						: $"Cannot leave {subject} partly moved", [], parameter);
			}
		}
	}
	
	private void CheckLoopRefill(LocalVariableSymbol binding, InitState state, MovePaths paths,
		HashSet<SourceLocation> reportedMoves)
	{
		var element = paths.GetRoot(binding).GetChild(new DerefProjection());
		foreach (var move in state.GetMoves(paths.GetRoot(binding)))
		{
			if (reportedMoves.Add(move.Location))
				Report(move.Location, move.Path == element && move.Depth == 0
					? "Cannot leave 'mut' loop variables moved"
					: "Cannot leave 'mut' loop variables partly moved", [], binding);
		}
	}
	
	private string? GetMoveError(Place place, MovePaths paths) => place.Root switch
	{
		GlobalSymbol => null,
		ParameterSymbol { Mode: ParameterMode.ReadOnly or ParameterMode.Imm } parameter
			when !typePool.IsCopy(paths.GetRoot(parameter).Type) => "Cannot move read-only parameters",
		LocalVariableSymbol { Captured: not null } => "Cannot move captured variables",
		LocalVariableSymbol { IsLoopBinding: true, IsMutBinding: true } when place.Path is [DerefProjection, ..] =>
			HasDestructorAbove(place, paths) ? "Cannot move fields out of values with destructors" : null,
		LocalVariableSymbol { IsLoopBinding: true } => "Cannot move loop variables",
		LocalVariableSymbol { IsBorrowBinding: true } => "Cannot move pattern bindings",
		_ when place.Path.Any(IsComputedIndex) => "Cannot move array elements at computed indices",
		_ when place.Root == destructorSelf && place.Path.IsEmpty => "Cannot move 'self' in destructors",
		_ when place.Root == destructorSelf && HasDestructorAbove(place, paths) =>
			"Cannot move fields out of values with destructors",
		ParameterSymbol { Mode: ParameterMode.Mut } => null,
		_ => HasDestructorAbove(place, paths) ? "Cannot move fields out of values with destructors" : null
	};
	
	private bool HasDestructorAbove(Place place, MovePaths paths)
	{
		var type = paths.GetRoot(place.Root).Type;
		var isOwnSelf = place.Root == ownSelf || place.Root == destructorSelf;
		foreach (var projection in place.Path)
		{
			if (type is RecordSymbol { HasDestructor: true } && !isOwnSelf)
				return true;
			
			isOwnSelf = false;
			
			switch (projection)
			{
				case FieldProjection p:
					type = typePool.GetTypeOfMember(p.Field);
					break;
				
				case IndexProjection when type is ArrayType array:
					type = array.ElementType;
					break;
				
				case DerefProjection when type is PointerType pointer:
					type = pointer.BaseType;
					break;
				
				default:
					return false;
			}
		}
		
		return false;
	}
	
	private void Report(SourceLocation location, string message, IEnumerable<string> hints, VariableSymbol? root)
	{
		if (root is not null)
			errorRoots.Add(root);
		
		if (pendingKeys.Add((location, message)))
			pending.Add(new(DiagnosticSeverity.Error, location, message) { Hints = [..hints] });
	}
	
	private static IEnumerable<string> DescribeMoves(SourceLocation location, IEnumerable<MoveSite> moves) =>
		DescribeSites(location, moves.Select(static move => move.Location), "Moved");
	
	private static IEnumerable<string> DescribeSites(SourceLocation location, IEnumerable<SourceLocation> sites,
		string action) => sites
		.OrderBy(static at => at.Range.Start)
		.Select(at => at == location
			? $"{action} here in an earlier iteration"
			: IsOnLaterLine(at, location)
				? $"{action} on line {at.GetLineColumn().Line} in an earlier iteration"
				: $"{action} on line {at.GetLineColumn().Line}")
		.Distinct();
	
	public static bool IsOnLaterLine(SourceLocation site, SourceLocation use) =>
		site.Source == use.Source && site.GetLineColumn().Line > use.GetLineColumn().Line;
	
	private Dictionary<BasicBlock, InitState> SolveInitialization(LoweredFunction function,
		Dictionary<BasicBlock, List<MemoryEvent>> events, MovePaths paths, InitState entryState)
	{
		var order = CfgUtils.GetReversePostorder(function);
		var entryStates = new Dictionary<BasicBlock, InitState>();
		var exitStates = new Dictionary<BasicBlock, InitState>();
		var changed = true;
		while (changed)
		{
			changed = false;
			foreach (var block in order)
			{
				var state = block == order[0] ? entryState.Copy() : new InitState(paths.Count, PathState.None);
				
				foreach (var predecessor in block.GetPredecessors())
				{
					if (CfgUtils.GetSuccessors(predecessor).Contains(block) &&
					    exitStates.TryGetValue(predecessor, out var exitState))
						state.JoinWith(exitState);
				}
				
				entryStates[block] = state.Copy();
				foreach (var memoryEvent in events[block])
					Transfer(state, memoryEvent, paths);
				
				if (!exitStates.TryGetValue(block, out var previous))
				{
					exitStates[block] = state;
					changed = true;
				}
				else if (previous.JoinWith(state))
					changed = true;
			}
		}
		
		return entryStates;
	}
	
	private InitState CreateEntryState(LoweredFunction function, MovePaths paths)
	{
		var state = new InitState(paths.Count, PathState.Unassigned);
		foreach (var parameter in function.Info.Symbol.Parameters)
			state.Set(paths.GetRoot(parameter), PathState.Initialized);
		
		if (function.Info.Symbol is { Kind: FunctionKind.Constructor, Parameters: [var self, ..] })
		{
			var root = paths.GetRoot(self);
			state.Set(root, PathState.Unassigned);
			ApplyPartDefaults(state, root);
		}
		
		return state;
	}
	
	private void SetDefaults(InitState state, MovePath path)
	{
		state.Set(path, PathState.Unassigned);
		ApplyDefaults(state, path);
	}
	
	private void ApplyRequiredDefaults(InitState state, Place place, MovePaths paths)
	{
		foreach (var path in paths.FindAncestors(place))
		{
			if (RequiresWrite(path))
				ApplyPartDefaults(state, path);
		}
	}
	
	private void ApplyDefaults(InitState state, MovePath path)
	{
		if (!RequiresWrite(path))
			ApplyPartDefaults(state, path);
	}
	
	private void ApplyPartDefaults(InitState state, MovePath path)
	{
		if (typePool.HasDefault(path.Type))
		{
			state.ApplyDefaults(path);
			return;
		}
		
		if (UntrackedPartsHaveDefaults(path))
			state.ApplyOwnDefault(path);
		
		foreach (var child in path.Children)
			ApplyDefaults(state, child);
	}
	
	private bool RequiresWrite(MovePath path) => RequiresWrite(path.Type, path.Projection);
	
	private bool RequiresWrite(TypeSymbol type, Projection? projection) =>
		projection is FieldProjection { Field.IsRequired: true } ||
		(type is RecordSymbol { HasDestructor: true } || typePool.DeclaresConstructors(type)) &&
		!typePool.HasDefault(type);
	
	private Shape WriteElement(Shape shape, TypeSymbol type, Projection[] suffix, int index, Projection? projection)
	{
		if (index == suffix.Length)
			return new LeafShape(PathState.Initialized, []);
		
		if (projection is not null && RequiresWrite(type, projection))
			shape = ApplyPartDefaults(shape, type);
		
		return _layout.Update(shape, type, suffix[index],
			(part, partType) => WriteElement(part, partType, suffix, index + 1, suffix[index]));
	}
	
	private Shape MoveElement(Shape shape, TypeSymbol type, Projection[] suffix, int index, MovePath array,
		SourceLocation location)
	{
		if (index == suffix.Length)
			return new LeafShape(PathState.Moved, [new MoveSite(array, location, index)]);
		
		return _layout.Update(shape, type, suffix[index],
			(part, partType) => MoveElement(part, partType, suffix, index + 1, array, location));
	}
	
	private Shape ApplyDefaults(Shape shape, TypeSymbol type, Projection? projection) =>
		RequiresWrite(type, projection) ? shape : ApplyPartDefaults(shape, type);
	
	private Shape ApplyPartDefaults(Shape shape, TypeSymbol type)
	{
		if (typePool.HasDefault(type))
			return Shape.MapLeaves(shape, static leaf => leaf.State.HasFlag(PathState.Unassigned)
				? new LeafShape(leaf.State & ~PathState.Unassigned | PathState.Initialized, leaf.Moves)
				: leaf);
		
		if (_layout.GetFields(type) is { } fields)
			return FieldsShape.Create([
				..fields.Select((field, i) => ApplyDefaults(FieldsShape.GetPart(shape, i),
					typePool.GetTypeOfMember(field),
					new FieldProjection(field)))
			]);
		
		if (type is not ArrayType array || ShapeLayout.GetLength(type) is not { } length)
			return shape;
		
		var elements = ElementsShape.Expand(shape, length);
		return ElementsShape.Create(elements.Runs.Select(run =>
			run with { Shape = ApplyDefaults(run.Shape, array.ElementType, null) }), length);
	}
	
	private bool UntrackedPartsHaveDefaults(MovePath path) =>
		path is { IsComplete: false, Type: RecordSymbol record } && typePool.GetMembers(record)
			.OfType<FieldSymbol>()
			.Where(field => path.GetChild(new FieldProjection(field)) is null)
			.All(typePool.HasDefault);
	
	private void Transfer(InitState state, MemoryEvent memoryEvent, MovePaths paths)
	{
		switch (memoryEvent)
		{
			case DefineEvent filled when filled.Local.IsFilledInPlace ||
			                             filled is { Kind: DefineKind.Default, Local.StartsZeroed: true }:
				state.Set(paths.GetRoot(filled.Local), PathState.Initialized);
				break;
			
			case DefineEvent { Kind: DefineKind.Default } e:
				SetDefaults(state, paths.GetRoot(e.Local));
				break;
			
			case DefineEvent e:
				state.Set(paths.GetRoot(e.Local), e.Kind == DefineKind.Undef ? PathState.Undef : PathState.Initialized);
				break;
			
			case WriteEvent e when FindElement(e.Place, paths) is var (array, depth) &&
			                       !IsComputedIndex(e.Place.Path[depth]):
				ApplyRequiredDefaults(state, e.Place, paths);
				state.SetElements(array, WriteElement(state.GetElements(array), array.Type, [..e.Place.Path[depth..]],
					0,
					null));
				
				break;
			
			case AccessEvent { Kind: AccessKind.Move } e when FindElement(e.Place, paths) is var (array, depth) &&
			                                                  !IsComputedIndex(e.Place.Path[depth]):
				if (GetMoveError(e.Place, paths) is null)
					state.SetElements(array, MoveElement(state.GetElements(array), array.Type,
						[..e.Place.Path[depth..]],
						0, array, e.Location));
				
				break;
			
			case WriteEvent e:
				ApplyRequiredDefaults(state, e.Place, paths);
				if (paths.Find(e.Place) is not { } target)
					break;
				
				if (IsDeferredRoot(e.Place))
					state.Write(target, e.Location);
				else
					state.Set(target, PathState.Initialized);
				
				break;
			
			case AccessEvent { Kind: AccessKind.Move } e when paths.Find(e.Place) is { } path:
				if (GetMoveError(e.Place, paths) is null)
					state.Move(path, e.Location);
				
				break;
			
			case AccessEvent { Kind: AccessKind.Address } e when FindFillable(e.Place, state, paths) is { } storage &&
			                                                     state.GetUninitialized(storage)
				                                                     .HasFlag(PathState.Undef):
				state.Set(storage, PathState.Initialized);
				break;
			
			case StorageDeadEvent e:
				state.Set(paths.GetRoot(e.Local), PathState.Unassigned);
				break;
		}
	}
}