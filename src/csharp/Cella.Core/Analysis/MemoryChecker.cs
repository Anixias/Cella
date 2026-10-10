using Cella.Core.Binding;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Analysis;

public sealed class MemoryChecker(TypePool typePool, DiagnosticList diagnostics)
{
	private readonly record struct Uninitialized(string Condition, IEnumerable<MoveSite> Moves);
	
	private ParameterSymbol? destructorSelf;
	
	public void Check(LoweredFunction function)
	{
		destructorSelf = function.Info.Symbol is { Kind: FunctionKind.Destructor, Parameters: [var self] }
			? self
			: null;
		
		AddDestructorExitDrops(function);
		var events = EventLinearizer.Linearize(function);
		var requiredFields = GetRequiredFields(function);
		var paths = MovePaths.Build(function, events.Values.SelectMany(static e => e), requiredFields, typePool);
		var entryState = CreateEntryState(function, paths);
		var reportedMoves = new HashSet<SourceLocation>();
		var drops = new Dictionary<DropInstruction, (MovePath Path, InitState State)>();
		InitState? returnState = null;
		var entryStates = SolveInitialization(function, events, paths, entryState);
		foreach (var (block, blockEntryState) in entryStates)
		{
			var state = blockEntryState.Copy();
			foreach (var memoryEvent in events[block])
			{
				switch (memoryEvent)
				{
					case AccessEvent access:
						CheckAccess(access, state, paths);
						break;
					
					case WriteEvent write when write.Place.Path.Any(IsComputedIndex):
						ApplyRequiredDefaults(state, write.Place, paths);
						if (FindUninitialized(write.Place, state, paths) is { } uninitialized)
							Report(write.Location,
								$"Cannot assign elements of {uninitialized.Condition} arrays at computed indices",
								DescribeMoves(write.Location, uninitialized.Moves));
						
						break;
					
					case WriteEvent write:
						CheckDeferredWrite(write, state, paths);
						break;
					
					case DropEvent drop when paths.Find(drop.Place) is { } path:
						drops[drop.Instruction] = (path, state.Copy());
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
		
		new BorrowChecker(typePool, diagnostics).Check(function, events);
		new DropElaborator(typePool, (state, memoryEvent) => Transfer(state, memoryEvent, paths))
			.Elaborate(function, entryState, entryStates, drops);
	}
	
	private void AddDestructorExitDrops(LoweredFunction function)
	{
		if (function.Info.Symbol is not { Kind: FunctionKind.Destructor, Parameters: [var self] } ||
		    function.Info.Signature.GetDeclaredType(0) is not RecordSymbol record)
			return;
		
		var movedFields = EventLinearizer.Linearize(function).Values
			.SelectMany(static events => events)
			.OfType<AccessEvent>()
			.Where(access => access is { Kind: AccessKind.Move, Place.Path: [FieldProjection] } &&
			                 access.Place.Root == self)
			.Select(static access => ((FieldProjection)access.Place.Path[0]).Field)
			.ToHashSet();
		
		typePool.SetDestructorMoves(record, movedFields);
		var selfValue = new VariableValue(new(self, function.Info.Signature.ParameterTypes[0]), SourceLocation.None);
		var target = new UnaryOpValue(record, selfValue, UnaryOperation.Dereference, SourceLocation.None);
		var fields = typePool.GetMembers(record).OfType<FieldSymbol>().Where(movedFields.Contains).Reverse().ToList();
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
			Report(constructor.Keyword.SourceLocation, "Cannot leave fields uninitialized", hints);
	}
	
	private void CheckAccess(AccessEvent access, InitState state, MovePaths paths)
	{
		if (access.Kind == AccessKind.Move && GetMoveError(access.Place, paths) is { } error)
		{
			Report(access.Location, error, []);
			return;
		}
		
		if (FindUninitialized(access.Place, state, paths) is { } uninitialized)
			Report(access.Location, $"Cannot use {uninitialized.Condition} values",
				DescribeMoves(access.Location, uninitialized.Moves));
		else if (access.IsImplicit)
			Report(access.Location, $"Cannot move '{access.Type.Name}' values implicitly", []);
	}
	
	private void CheckDeferredWrite(WriteEvent write, InitState state, MovePaths paths)
	{
		if (write.Place is not { Root: LocalVariableSymbol { IsDeferred: true } local, Path.IsEmpty: true })
			return;
		
		var path = paths.GetRoot(local);
		if ((state.GetOwnState(path) & (PathState.Initialized | PathState.Moved)) != 0)
			Report(write.Location, "Cannot reassign values",
				DescribeSites(write.Location, state.GetOwnWrites(path), "Assigned"));
	}
	
	private static Uninitialized? FindUninitialized(Place place, InitState state, MovePaths paths)
	{
		if (paths.FindPrefix(place) is not ({ } path, var depth))
			return null;
		
		var isWhole = depth == place.Path.Length || IsComputedIndex(place.Path[depth]);
		if (isWhole ? state.IsWhollyInitialized(path) : state.GetOwnState(path) == PathState.Initialized)
			return null;
		
		var uninitialized = isWhole ? state.GetUninitialized(path) : state.GetOwnState(path) & ~PathState.Initialized;
		var isPartial = isWhole && state.HasInitializedPart(path);
		if (!uninitialized.HasFlag(PathState.Moved))
			return new(isPartial ? "partly initialized" : "uninitialized", []);
		
		return new(isPartial ? "partly moved" : "moved", isWhole ? state.GetMoves(path) : state.GetOwnMoves(path));
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
				if (parameter == destructorSelf && root.Children.Contains(move.Path))
					continue;
				
				if (reportedMoves.Add(move.Location))
					Report(move.Location, move.Path == root
						? $"Cannot leave {subject} moved"
						: $"Cannot leave {subject} partly moved", []);
			}
		}
	}
	
	private string? GetMoveError(Place place, MovePaths paths) => place.Root switch
	{
		ParameterSymbol { Mode: ParameterMode.ReadOnly } parameter
			when !typePool.IsCopy(paths.GetRoot(parameter).Type) => "Cannot move read-only parameters",
		LocalVariableSymbol { Captured: not null } => "Cannot move captured variables",
		LocalVariableSymbol { IsLoopBinding: true } => "Cannot move loop variables",
		LocalVariableSymbol { IsBorrowBinding: true } => "Cannot move pattern bindings",
		_ when place.Path.Any(IsComputedIndex) => "Cannot move array elements at computed indices",
		_ when place.Root == destructorSelf && place.Path.IsEmpty => "Cannot move 'self' in destructors",
		ParameterSymbol { Mode: ParameterMode.Mut } => null,
		_ => HasDestructorAbove(place, paths) ? "Cannot move fields out of values with destructors" : null
	};
	
	private bool HasDestructorAbove(Place place, MovePaths paths)
	{
		var type = paths.GetRoot(place.Root).Type;
		foreach (var projection in place.Path)
		{
			if (type is RecordSymbol { HasDestructor: true })
				return true;
			
			switch (projection)
			{
				case FieldProjection p:
					type = typePool.GetTypeOfMember(p.Field);
					break;
				
				case IndexProjection when type is ArrayType array:
					type = array.ElementType;
					break;
				
				default:
					return false;
			}
		}
		
		return false;
	}
	
	private void Report(SourceLocation location, string message, IEnumerable<string> hints) =>
		diagnostics.Add(new(DiagnosticSeverity.Error, location, message) { Hints = [..hints] });
	
	private static IEnumerable<string> DescribeMoves(SourceLocation location, IEnumerable<MoveSite> moves) =>
		DescribeSites(location, moves.Select(static move => move.Location), "Moved");
	
	private static IEnumerable<string> DescribeSites(SourceLocation location, IEnumerable<SourceLocation> sites,
		string action) => sites
		.OrderBy(static at => at.Range.Start)
		.Select(at => at == location
			? $"{action} here in an earlier iteration"
			: $"{action} on line {at.GetLineColumn().Line}")
		.Distinct();
	
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
					if (exitStates.TryGetValue(predecessor, out var exitState))
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
	
	private static bool RequiresWrite(MovePath path) => path is
		{ Projection: FieldProjection { Field.IsRequired: true } } or { Type: RecordSymbol { HasDestructor: true } };
	
	private bool UntrackedPartsHaveDefaults(MovePath path) =>
		path is { IsComplete: false, Type: RecordSymbol record } && typePool.GetMembers(record)
			.OfType<FieldSymbol>()
			.Where(field => path.GetChild(new FieldProjection(field)) is null)
			.All(typePool.HasDefault);
	
	private void Transfer(InitState state, MemoryEvent memoryEvent, MovePaths paths)
	{
		switch (memoryEvent)
		{
			case DefineEvent { Kind: DefineKind.Default } e:
				SetDefaults(state, paths.GetRoot(e.Local));
				break;
			
			case DefineEvent e:
				state.Set(paths.GetRoot(e.Local), e.Kind == DefineKind.Undef ? PathState.Undef : PathState.Initialized);
				break;
			
			case WriteEvent e:
				ApplyRequiredDefaults(state, e.Place, paths);
				if (paths.Find(e.Place) is { } target)
					state.Write(target, e.Location);
				
				break;
			
			case AccessEvent { Kind: AccessKind.Move } e when paths.Find(e.Place) is { } path:
				if (GetMoveError(e.Place, paths) is null)
					state.Move(path, e.Location);
				
				break;
			
			case StorageDeadEvent e:
				state.Set(paths.GetRoot(e.Local), PathState.Unassigned);
				break;
		}
	}
}