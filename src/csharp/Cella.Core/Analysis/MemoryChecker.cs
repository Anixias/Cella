using Cella.Core.Binding;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Analysis;

public sealed class MemoryChecker(TypePool typePool, DiagnosticList diagnostics)
{
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
		
		if (paths.FindPrefix(access.Place) is not ({ } path, var depth))
			return;
		
		var projections = access.Place.Path;
		var isWhole = depth == projections.Length || projections[depth] is IndexProjection { Index: null };
		if (isWhole ? state.IsWhollyInitialized(path) : state.GetOwnState(path) == PathState.Initialized)
			return;
		
		var uninitialized = isWhole ? state.GetUninitialized(path) : state.GetOwnState(path) & ~PathState.Initialized;
		var isPartial = isWhole && state.HasInitializedPart(path);
		if (uninitialized.HasFlag(PathState.Moved))
			Report(access.Location, isPartial ? "Cannot use partly moved values" : "Cannot use moved values",
				isWhole ? state.GetMoves(path) : state.GetOwnMoves(path));
		else
			Report(access.Location,
				isPartial ? "Cannot use partly initialized values" : "Cannot use uninitialized values", []);
	}
	
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
		LocalVariableSymbol { IsPatternBinding: true } binding
			when binding.IsMutBinding || !typePool.IsCopy(binding.Type) => "Cannot move pattern bindings",
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
	
	private void Report(SourceLocation location, string message, IEnumerable<MoveSite> moves) =>
		diagnostics.Add(new(DiagnosticSeverity.Error, location, message)
		{
			Hints =
			[
				..moves
					.Select(static move => move.Location)
					.OrderBy(static at => at.Range.Start)
					.Select(at => at == location
						? "Moved here in an earlier iteration"
						: $"Moved on line {at.GetLineColumn().Line}")
					.Distinct()
			]
		});
	
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
				state.Set(path, PathState.Initialized);
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