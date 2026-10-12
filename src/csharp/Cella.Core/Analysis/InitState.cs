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
	private const int ChunkSize = 64;
	
	private readonly int _count;
	private readonly Chunk[] _chunks;
	private readonly bool[] _owned;
	
	public InitState(int count, PathState state)
	{
		_count = count;
		_chunks = new Chunk[(count + ChunkSize - 1) / ChunkSize];
		_owned = new bool[_chunks.Length];
		Array.Fill(_chunks, state == PathState.None ? Chunk.Empty : Chunk.Filled(state));
	}
	
	private InitState(int count, Chunk[] chunks)
	{
		_count = count;
		_chunks = chunks;
		_owned = new bool[chunks.Length];
	}
	
	public InitState Copy()
	{
		Array.Fill(_owned, false);
		return new(_count, (Chunk[])_chunks.Clone());
	}
	
	public bool JoinWith(InitState other)
	{
		var changed = false;
		for (var c = 0; c < _chunks.Length; c++)
		{
			var mine = _chunks[c];
			var theirs = other._chunks[c];
			if (mine == theirs || theirs == Chunk.Empty)
				continue;
			
			var length = GetChunkLength(c);
			if (mine != Chunk.Empty && Chunk.Covers(mine, theirs, length))
				continue;
			
			changed = true;
			if (mine == Chunk.Empty || Chunk.Covers(theirs, mine, length))
			{
				_chunks[c] = theirs;
				_owned[c] = false;
				other._owned[c] = false;
				continue;
			}
			
			var target = Own(c);
			for (var i = 0; i < length; i++)
			{
				target.States[i] |= theirs.States[i];
				target.Moves[i] = Union(target.Moves[i], theirs.Moves[i]);
				target.Writes[i] = Union(target.Writes[i], theirs.Writes[i]);
			}
		}
		
		return changed;
	}
	
	public void Set(MovePath path, PathState state) => Fill(path.Index, path.End, state,
		ImmutableHashSet<MoveSite>.Empty, ImmutableHashSet<SourceLocation>.Empty);
	
	public void Write(MovePath path, SourceLocation location) => Fill(path.Index, path.End, PathState.Initialized,
		ImmutableHashSet<MoveSite>.Empty, [location]);
	
	public void Move(MovePath path, SourceLocation location) =>
		Fill(path.Index, path.End, PathState.Moved, [new(path, location)], null);
	
	public void ApplyDefaults(MovePath path) => ApplyDefaults(path.Index, path.End);
	
	public void ApplyOwnDefault(MovePath path) => ApplyDefaults(path.Index, path.Index + 1);
	
	public PathState GetOwnState(MovePath path) => GetState(path.Index);
	
	public IEnumerable<MoveSite> GetOwnMoves(MovePath path) => GetChunk(path.Index).Moves[path.Index % ChunkSize];
	
	public IEnumerable<SourceLocation> GetOwnWrites(MovePath path) =>
		GetChunk(path.Index).Writes[path.Index % ChunkSize];
	
	public IEnumerable<MoveSite> GetMoves(MovePath path) => Enumerable.Range(path.Index, path.End - path.Index)
		.SelectMany(index => GetChunk(index).Moves[index % ChunkSize])
		.Distinct();
	
	public bool HasInitializedPart(MovePath path) =>
		!path.IsComplete && GetState(path.Index) == PathState.Initialized || path.Children.Any(HasInitializedPart);
	
	public bool IsWhollyInitialized(MovePath path) =>
		(path.IsComplete || GetState(path.Index) == PathState.Initialized) && path.Children.All(IsWhollyInitialized);
	
	public bool IsUninitialized(MovePath path) =>
		(path.IsComplete || !GetState(path.Index).HasFlag(PathState.Initialized)) &&
		path.Children.All(IsUninitialized);
	
	public PathState GetUninitialized(MovePath path) => path.Children.Aggregate(
		path.IsComplete ? PathState.None : GetState(path.Index) & ~PathState.Initialized,
		(state, child) => state | GetUninitialized(child));
	
	private Chunk GetChunk(int index) => _chunks[index / ChunkSize];
	
	private PathState GetState(int index) => GetChunk(index).States[index % ChunkSize];
	
	private int GetChunkLength(int chunk) => Math.Min(ChunkSize, _count - chunk * ChunkSize);
	
	private Chunk Own(int chunk)
	{
		if (_owned[chunk])
			return _chunks[chunk];
		
		_owned[chunk] = true;
		return _chunks[chunk] = _chunks[chunk].Clone();
	}
	
	private void Fill(int start, int end, PathState state, ImmutableHashSet<MoveSite> moves,
		ImmutableHashSet<SourceLocation>? writes)
	{
		for (var index = start; index < end;)
		{
			var offset = index % ChunkSize;
			var count = Math.Min(ChunkSize - offset, end - index);
			var chunk = Own(index / ChunkSize);
			chunk.States.AsSpan(offset, count).Fill(state);
			chunk.Moves.AsSpan(offset, count).Fill(moves);
			if (writes is not null)
				chunk.Writes.AsSpan(offset, count).Fill(writes);
			
			index += count;
		}
	}
	
	private void ApplyDefaults(int start, int end)
	{
		for (var index = start; index < end; index++)
		{
			if (!GetState(index).HasFlag(PathState.Unassigned))
				continue;
			
			var states = Own(index / ChunkSize).States;
			states[index % ChunkSize] = states[index % ChunkSize] & ~PathState.Unassigned | PathState.Initialized;
		}
	}
	
	private static ImmutableHashSet<T> Union<T>(ImmutableHashSet<T> first, ImmutableHashSet<T> second) =>
		second.IsEmpty || first == second ? first : first.IsEmpty ? second : first.Union(second);
	
	private sealed class Chunk
	(
		PathState[] states,
		ImmutableHashSet<MoveSite>[] moves,
		ImmutableHashSet<SourceLocation>[] writes
	)
	{
		public PathState[] States { get; } = states;
		public ImmutableHashSet<MoveSite>[] Moves { get; } = moves;
		public ImmutableHashSet<SourceLocation>[] Writes { get; } = writes;
		
		public static Chunk Empty { get; } = Filled(PathState.None);
		
		public static Chunk Filled(PathState state)
		{
			var chunk = new Chunk(new PathState[ChunkSize], new ImmutableHashSet<MoveSite>[ChunkSize],
				new ImmutableHashSet<SourceLocation>[ChunkSize]);
			
			Array.Fill(chunk.States, state);
			Array.Fill(chunk.Moves, ImmutableHashSet<MoveSite>.Empty);
			Array.Fill(chunk.Writes, ImmutableHashSet<SourceLocation>.Empty);
			return chunk;
		}
		
		public Chunk Clone() => new((PathState[])States.Clone(), (ImmutableHashSet<MoveSite>[])Moves.Clone(),
			(ImmutableHashSet<SourceLocation>[])Writes.Clone());
		
		public static bool Covers(Chunk outer, Chunk inner, int length)
		{
			for (var i = 0; i < length; i++)
			{
				if ((outer.States[i] | inner.States[i]) != outer.States[i] ||
				    !Contains(outer.Moves[i], inner.Moves[i]) || !Contains(outer.Writes[i], inner.Writes[i]))
					return false;
			}
			
			return true;
		}
		
		private static bool Contains<T>(ImmutableHashSet<T> outer, ImmutableHashSet<T> inner) =>
			inner.IsEmpty || outer == inner || outer.IsSupersetOf(inner);
	}
}