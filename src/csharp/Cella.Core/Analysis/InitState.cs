using System.Collections.Immutable;
using Cella.Core.Text;

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
public readonly record struct MoveSite(MovePath Path, SourceLocation Location);

public sealed class InitState
{
	private readonly PathState[] _states;
	private readonly ImmutableHashSet<MoveSite>[] _moves;
	
	public InitState(int count, PathState state)
	{
		_states = new PathState[count];
		_moves = new ImmutableHashSet<MoveSite>[count];
		Array.Fill(_states, state);
		Array.Fill(_moves, ImmutableHashSet<MoveSite>.Empty);
	}
	
	private InitState(PathState[] states, ImmutableHashSet<MoveSite>[] moves)
	{
		_states = states;
		_moves = moves;
	}
	
	public InitState Copy() => new((PathState[])_states.Clone(), (ImmutableHashSet<MoveSite>[])_moves.Clone());
	
	public bool JoinWith(InitState other)
	{
		var changed = false;
		for (var i = 0; i < _states.Length; i++)
		{
			var joined = _states[i] | other._states[i];
			var moves = _moves[i].Union(other._moves[i]);
			changed |= joined != _states[i] || moves.Count != _moves[i].Count;
			_states[i] = joined;
			_moves[i] = moves;
		}
		
		return changed;
	}
	
	public void Set(MovePath path, PathState state) => Fill(path, state, ImmutableHashSet<MoveSite>.Empty);
	
	public void Move(MovePath path, SourceLocation location) => Fill(path, PathState.Moved, [new(path, location)]);
	
	public PathState GetOwnState(MovePath path) => _states[path.Index];
	
	public IEnumerable<MoveSite> GetOwnMoves(MovePath path) => _moves[path.Index];
	
	public IEnumerable<MoveSite> GetMoves(MovePath path) =>
		_moves.Skip(path.Index).Take(path.End - path.Index).SelectMany(static moves => moves).Distinct();
	
	public DropState GetDropState(MovePath path) => new(Classify(path), GetUninitialized(path));
	
	private void Fill(MovePath path, PathState state, ImmutableHashSet<MoveSite> moves)
	{
		var count = path.End - path.Index;
		_states.AsSpan(path.Index, count).Fill(state);
		_moves.AsSpan(path.Index, count).Fill(moves);
	}
	
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
	
	public bool HasInitializedPart(MovePath path) =>
		!path.IsComplete && _states[path.Index] == PathState.Initialized || path.Children.Any(HasInitializedPart);
	
	public bool IsWhollyInitialized(MovePath path) =>
		(path.IsComplete || _states[path.Index] == PathState.Initialized) && path.Children.All(IsWhollyInitialized);
	
	private bool IsUninitialized(MovePath path) =>
		(path.IsComplete || !_states[path.Index].HasFlag(PathState.Initialized)) && path.Children.All(IsUninitialized);
	
	public PathState GetUninitialized(MovePath path) => path.Children.Aggregate(
		path.IsComplete ? PathState.None : _states[path.Index] & ~PathState.Initialized,
		(state, child) => state | GetUninitialized(child));
}