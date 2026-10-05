namespace Cella.Core.Analysis;

[Flags]
public enum PathState : byte
{
	None = 0,
	Initialized = 1,
	Unassigned = 2,
	Undef = 4,
	Moved = 8
}

public enum Initialization
{
	Whole,
	Partial,
	Maybe,
	None
}

public readonly record struct DropState(Initialization Initialization, PathState Uninitialized);

public sealed class InitState
{
	private readonly PathState[] _states;
	
	public InitState(int count, PathState state)
	{
		_states = new PathState[count];
		Array.Fill(_states, state);
	}
	
	private InitState(PathState[] states)
	{
		_states = states;
	}
	
	public InitState Copy() => new((PathState[])_states.Clone());
	
	public bool JoinWith(InitState other)
	{
		var changed = false;
		for (var i = 0; i < _states.Length; i++)
		{
			var joined = _states[i] | other._states[i];
			changed |= joined != _states[i];
			_states[i] = joined;
		}
		
		return changed;
	}
	
	public void Set(MovePath path, PathState state) => _states.AsSpan(path.Index, path.End - path.Index).Fill(state);
	
	public DropState GetDropState(MovePath path) => new(Classify(path), GetUninitialized(path));
	
	private Initialization Classify(MovePath path)
	{
		if (IsWhollyInitialized(path))
			return Initialization.Whole;
		
		if (IsUninitialized(path))
			return Initialization.None;
		
		return IsInitialized(path) ? Initialization.Partial : Initialization.Maybe;
	}
	
	private bool IsInitialized(MovePath path) => _states[path.Index] == PathState.Initialized ||
	                                             path.IsComplete && path.Children.All(IsInitialized);
	
	private bool IsWhollyInitialized(MovePath path) =>
		(path.IsComplete || _states[path.Index] == PathState.Initialized) && path.Children.All(IsWhollyInitialized);
	
	private bool IsUninitialized(MovePath path) =>
		(path.IsComplete || !_states[path.Index].HasFlag(PathState.Initialized)) && path.Children.All(IsUninitialized);
	
	private PathState GetUninitialized(MovePath path) => path.Children.Aggregate(
		path.IsComplete ? PathState.None : _states[path.Index] & ~PathState.Initialized,
		(state, child) => state | GetUninitialized(child));
}