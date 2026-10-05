using Cella.Core.Binding;
using Cella.Core.Lowering;

namespace Cella.Core.Analysis;

public sealed class MemoryChecker(TypePool typePool)
{
	private readonly Dictionary<DropInstruction, DropState> _dropStates = [];
	
	public IReadOnlyDictionary<DropInstruction, DropState> DropStates => _dropStates;
	
	public void Check(LoweredFunction function)
	{
		var events = EventLinearizer.Linearize(function);
		var paths = MovePaths.Build(function, events.Values.SelectMany(static e => e), typePool);
		foreach (var (block, state) in SolveInitialization(function, events, paths))
		{
			foreach (var memoryEvent in events[block])
			{
				if (memoryEvent is DropEvent drop && paths.Find(drop.Place) is { } path)
					_dropStates[drop.Instruction] = state.GetDropState(path);
				
				Transfer(state, memoryEvent, paths);
			}
		}
	}
	
	private static Dictionary<BasicBlock, InitState> SolveInitialization(LoweredFunction function,
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
	
	private static void Transfer(InitState state, MemoryEvent memoryEvent, MovePaths paths)
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
				state.Set(path, PathState.Moved);
				break;
			
			case StorageDeadEvent e:
				state.Set(paths.GetRoot(e.Local), PathState.Unassigned);
				break;
		}
	}
}