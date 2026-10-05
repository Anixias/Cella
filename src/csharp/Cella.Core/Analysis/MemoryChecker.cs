using Cella.Core.Binding;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Analysis;

public sealed class MemoryChecker(TypePool typePool, DiagnosticList diagnostics)
{
	private readonly record struct Uninitialized(string Condition, IEnumerable<MoveSite> Moves);
	
	private readonly Dictionary<DropInstruction, DropState> _dropStates = [];
	
	public IReadOnlyDictionary<DropInstruction, DropState> DropStates => _dropStates;
	
	public void Check(LoweredFunction function)
	{
		var events = EventLinearizer.Linearize(function);
		var paths = MovePaths.Build(function, events.Values.SelectMany(static e => e), typePool);
		var reportedMoves = new HashSet<SourceLocation>();
		foreach (var (block, state) in SolveInitialization(function, events, paths))
		{
			foreach (var memoryEvent in events[block])
			{
				switch (memoryEvent)
				{
					case AccessEvent access:
						CheckAccess(access, state, paths);
						break;
					
					case WriteEvent write when write.Place.Path.Any(IsComputedIndex):
						if (FindUninitialized(write.Place, state, paths) is { } uninitialized)
							Report(write.Location,
								$"Cannot assign elements of {uninitialized.Condition} arrays at computed indices",
								DescribeMoves(write.Location, uninitialized.Moves));
						
						break;
					
					case WriteEvent write:
						CheckDeferredWrite(write, state, paths);
						break;
					
					case DropEvent drop when paths.Find(drop.Place) is { } path:
						_dropStates[drop.Instruction] = state.GetDropState(path);
						break;
				}
				
				Transfer(state, memoryEvent, paths);
			}
			
			if (block.Terminator is ReturnTerminator)
				CheckRefills(function, state, paths, reportedMoves);
		}
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
		foreach (var parameter in function.Info.Symbol.Parameters)
		{
			if (parameter.Mode != ParameterMode.Mut)
				continue;
			
			var root = paths.GetRoot(parameter);
			foreach (var move in state.GetMoves(root))
			{
				if (reportedMoves.Add(move.Location))
					Report(move.Location, move.Path == root
						? "Cannot leave 'mut' parameters moved"
						: "Cannot leave 'mut' parameters partly moved", []);
			}
		}
	}
	
	private string? GetMoveError(Place place, MovePaths paths) => place.Root switch
	{
		ParameterSymbol { Mode: ParameterMode.ReadOnly } parameter
			when !typePool.IsCopy(paths.GetRoot(parameter).Type) => "Cannot move read-only parameters",
		LocalVariableSymbol { IsBorrowBinding: true } => "Cannot move pattern bindings",
		_ when place.Path.Any(IsComputedIndex) => "Cannot move array elements at computed indices",
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
		Dictionary<BasicBlock, List<MemoryEvent>> events, MovePaths paths)
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
				var state = block == order[0]
					? CreateEntryState(function, paths)
					: new InitState(paths.Count, PathState.None);
				
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
	
	private static InitState CreateEntryState(LoweredFunction function, MovePaths paths)
	{
		var state = new InitState(paths.Count, PathState.Unassigned);
		foreach (var parameter in function.Info.Symbol.Parameters)
			state.Set(paths.GetRoot(parameter), PathState.Initialized);
		
		return state;
	}
	
	private void Transfer(InitState state, MemoryEvent memoryEvent, MovePaths paths)
	{
		switch (memoryEvent)
		{
			case DefineEvent e:
				state.Set(paths.GetRoot(e.Local), e.IsUndef ? PathState.Undef : PathState.Initialized);
				break;
			
			case WriteEvent e when paths.Find(e.Place) is { } path:
				state.Write(path, e.Location);
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