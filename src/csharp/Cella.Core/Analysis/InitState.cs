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

public readonly record struct MoveSite(MovePath Path, SourceLocation Location);

public sealed class InitState
{
	private readonly PathState[] _states;
	private readonly ImmutableHashSet<MoveSite>[] _moves;
	private readonly ImmutableHashSet<SourceLocation>[] _writes;
	
	public InitState(int count, PathState state)
	{
		_states = new PathState[count];
		_moves = new ImmutableHashSet<MoveSite>[count];
		_writes = new ImmutableHashSet<SourceLocation>[count];
		Array.Fill(_states, state);
		Array.Fill(_moves, ImmutableHashSet<MoveSite>.Empty);
		Array.Fill(_writes, ImmutableHashSet<SourceLocation>.Empty);
	}
	
	private InitState(PathState[] states, ImmutableHashSet<MoveSite>[] moves,
		ImmutableHashSet<SourceLocation>[] writes)
	{
		_states = states;
		_moves = moves;
		_writes = writes;
	}
	
	public InitState Copy() => new((PathState[])_states.Clone(), (ImmutableHashSet<MoveSite>[])_moves.Clone(),
		(ImmutableHashSet<SourceLocation>[])_writes.Clone());
	
	public bool JoinWith(InitState other)
	{
		var changed = false;
		for (var i = 0; i < _states.Length; i++)
		{
			var joined = _states[i] | other._states[i];
			var moves = _moves[i].Union(other._moves[i]);
			var writes = _writes[i].Union(other._writes[i]);
			changed |= joined != _states[i] || moves.Count != _moves[i].Count || writes.Count != _writes[i].Count;
			_states[i] = joined;
			_moves[i] = moves;
			_writes[i] = writes;
		}
		
		return changed;
	}
	
	public void Set(MovePath path, PathState state)
	{
		Fill(path, state, ImmutableHashSet<MoveSite>.Empty);
		_writes.AsSpan(path.Index, path.End - path.Index).Fill(ImmutableHashSet<SourceLocation>.Empty);
	}
	
	public void Write(MovePath path, SourceLocation location)
	{
		Fill(path, PathState.Initialized, ImmutableHashSet<MoveSite>.Empty);
		_writes.AsSpan(path.Index, path.End - path.Index).Fill([location]);
	}
	
	public void Move(MovePath path, SourceLocation location) => Fill(path, PathState.Moved, [new(path, location)]);
	
	public void ApplyDefaults(MovePath path) => ApplyDefaults(path.Index, path.End);
	
	public void ApplyOwnDefault(MovePath path) => ApplyDefaults(path.Index, path.Index + 1);
	
	public PathState GetOwnState(MovePath path) => _states[path.Index];
	
	public IEnumerable<MoveSite> GetOwnMoves(MovePath path) => _moves[path.Index];
	
	public IEnumerable<SourceLocation> GetOwnWrites(MovePath path) => _writes[path.Index];
	
	public IEnumerable<MoveSite> GetMoves(MovePath path) =>
		_moves.Skip(path.Index).Take(path.End - path.Index).SelectMany(static moves => moves).Distinct();
	
	private void Fill(MovePath path, PathState state, ImmutableHashSet<MoveSite> moves)
	{
		var count = path.End - path.Index;
		_states.AsSpan(path.Index, count).Fill(state);
		_moves.AsSpan(path.Index, count).Fill(moves);
	}
	
	private void ApplyDefaults(int start, int end)
	{
		for (var i = start; i < end; i++)
		{
			if (_states[i].HasFlag(PathState.Unassigned))
				_states[i] = _states[i] & ~PathState.Unassigned | PathState.Initialized;
		}
	}
	
	public bool HasInitializedPart(MovePath path) =>
		!path.IsComplete && _states[path.Index] == PathState.Initialized || path.Children.Any(HasInitializedPart);
	
	public bool IsWhollyInitialized(MovePath path) =>
		(path.IsComplete || _states[path.Index] == PathState.Initialized) && path.Children.All(IsWhollyInitialized);
	
	public bool IsUninitialized(MovePath path) =>
		(path.IsComplete || !_states[path.Index].HasFlag(PathState.Initialized)) && path.Children.All(IsUninitialized);
	
	public PathState GetUninitialized(MovePath path) => path.Children.Aggregate(
		path.IsComplete ? PathState.None : _states[path.Index] & ~PathState.Initialized,
		(state, child) => state | GetUninitialized(child));
}