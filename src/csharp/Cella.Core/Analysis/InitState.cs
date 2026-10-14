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

public readonly record struct MoveSite(MovePath Path, SourceLocation Location, int Depth = 0);

public sealed class InitState
{
	private const int ChunkSize = 64;
	
	private readonly int _count;
	private readonly Chunk[] _chunks;
	private readonly bool[] _owned;
	private ImmutableDictionary<int, Shape> shapes = ImmutableDictionary<int, Shape>.Empty;
	
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
		return new(_count, (Chunk[])_chunks.Clone()) { shapes = shapes };
	}
	
	public bool JoinWith(InitState other)
	{
		var changed = JoinShapes(other);
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
				target.SetMoves(i, Union(target.GetMoves(i), theirs.GetMoves(i)));
				target.SetWrites(i, Union(target.GetWrites(i), theirs.GetWrites(i)));
			}
		}
		
		return changed;
	}
	
	public Shape GetElements(MovePath path) => shapes.TryGetValue(path.Index, out var shape)
		? shape
		: new LeafShape(GetState(path.Index), GetChunk(path.Index).GetMoves(path.Index % ChunkSize));
	
	public void SetElements(MovePath path, Shape shape)
	{
		Fill(path.Index, path.End, Shape.Summarize(shape), Shape.GetMoves(shape), null);
		if (shape is not LeafShape)
			shapes = shapes.SetItem(path.Index, shape);
	}
	
	public bool IsSameOutside(InitState other, IReadOnlyCollection<MovePath> excluded)
	{
		for (var index = 0; index < _count; index++)
		{
			if (excluded.Any(path => index >= path.Index && index < path.End))
				continue;
			
			var mine = GetChunk(index);
			var theirs = other.GetChunk(index);
			var offset = index % ChunkSize;
			if (mine != theirs && (mine.States[offset] != theirs.States[offset] ||
			                       !mine.GetMoves(offset).SetEquals(theirs.GetMoves(offset)) ||
			                       !mine.GetWrites(offset).SetEquals(theirs.GetWrites(offset))))
				return false;
			
			if (shapes.TryGetValue(index, out var shape) != other.shapes.TryGetValue(index, out var otherShape) ||
			    shape is not null && !shape.Equals(otherShape))
				return false;
		}
		
		return true;
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
	
	public IEnumerable<MoveSite> GetOwnMoves(MovePath path) => GetChunk(path.Index).GetMoves(path.Index % ChunkSize);
	
	public IEnumerable<SourceLocation> GetOwnWrites(MovePath path) =>
		GetChunk(path.Index).GetWrites(path.Index % ChunkSize);
	
	public IEnumerable<MoveSite> GetMoves(MovePath path) => Enumerable.Range(path.Index, path.End - path.Index)
		.SelectMany(index => GetChunk(index).GetMoves(index % ChunkSize))
		.Distinct();
	
	public bool HasInitializedPart(MovePath path) =>
		!path.IsComplete && (GetState(path.Index) == PathState.Initialized ||
		                     shapes.TryGetValue(path.Index, out var shape) && Shape.HasInitializedPart(shape)) ||
		path.Children.Any(HasInitializedPart);
	
	public bool IsWhollyInitialized(MovePath path) =>
		(path.IsComplete || GetState(path.Index) == PathState.Initialized) && path.Children.All(IsWhollyInitialized);
	
	public bool IsUninitialized(MovePath path) =>
		(path.IsComplete || !GetState(path.Index).HasFlag(PathState.Initialized)) &&
		path.Children.All(IsUninitialized);
	
	public PathState GetUninitialized(MovePath path) => path.Children.Aggregate(
		path.IsComplete ? PathState.None : GetState(path.Index) & ~PathState.Initialized,
		(state, child) => state | GetUninitialized(child));
	
	private bool JoinShapes(InitState other)
	{
		if (shapes.IsEmpty && other.shapes.IsEmpty)
			return false;
		
		var changed = false;
		foreach (var index in shapes.Keys.Union(other.shapes.Keys).ToList())
		{
			var mine = GetElementsAt(index);
			var joined = Shape.Join(mine, other.GetElementsAt(index));
			if (joined.Equals(mine))
				continue;
			
			changed = true;
			shapes = joined is LeafShape ? shapes.Remove(index) : shapes.SetItem(index, joined);
		}
		
		return changed;
	}
	
	private Shape GetElementsAt(int index) => shapes.TryGetValue(index, out var shape)
		? shape
		: new LeafShape(GetState(index), GetChunk(index).GetMoves(index % ChunkSize));
	
	private void RemoveShapes(int start, int end)
	{
		if (shapes.IsEmpty)
			return;
		
		foreach (var index in shapes.Keys.Where(index => index >= start && index < end).ToList())
			shapes = shapes.Remove(index);
	}
	
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
		RemoveShapes(start, end);
		for (var index = start; index < end;)
		{
			var offset = index % ChunkSize;
			var count = Math.Min(ChunkSize - offset, end - index);
			var chunk = Own(index / ChunkSize);
			chunk.States.AsSpan(offset, count).Fill(state);
			chunk.FillMoves(offset, count, moves);
			if (writes is not null)
				chunk.FillWrites(offset, count, writes);
			
			index += count;
		}
	}
	
	private void ApplyDefaults(int start, int end)
	{
		foreach (var (index, shape) in shapes.Where(entry => entry.Key >= start && entry.Key < end).ToList())
		{
			var applied = Shape.MapLeaves(shape, static leaf => leaf.State.HasFlag(PathState.Unassigned)
				? new LeafShape(leaf.State & ~PathState.Unassigned | PathState.Initialized, leaf.Moves)
				: leaf);
			
			shapes = applied is LeafShape ? shapes.Remove(index) : shapes.SetItem(index, applied);
		}
		
		for (var index = start; index < end; index++)
		{
			if (!GetState(index).HasFlag(PathState.Unassigned))
				continue;
			
			var states = Own(index / ChunkSize).States;
			states[index % ChunkSize] = states[index % ChunkSize] & ~PathState.Unassigned | PathState.Initialized;
		}
	}
	
	private static ImmutableHashSet<T> Union<T>(ImmutableHashSet<T> first, ImmutableHashSet<T> second) =>
		Shape.Union(first, second);
	
	private sealed class Chunk
	(
		PathState[] states,
		ImmutableHashSet<MoveSite>[]? moves,
		ImmutableHashSet<SourceLocation>[]? writes
	)
	{
		private ImmutableHashSet<MoveSite>[]? moves = moves;
		private ImmutableHashSet<SourceLocation>[]? writes = writes;
		
		public PathState[] States { get; } = states;
		
		public static Chunk Empty { get; } = Filled(PathState.None);
		
		public static Chunk Filled(PathState state)
		{
			var chunk = new Chunk(new PathState[ChunkSize], null, null);
			Array.Fill(chunk.States, state);
			return chunk;
		}
		
		public ImmutableHashSet<MoveSite> GetMoves(int index) => moves?[index] ?? ImmutableHashSet<MoveSite>.Empty;
		
		public ImmutableHashSet<SourceLocation> GetWrites(int index) =>
			writes?[index] ?? ImmutableHashSet<SourceLocation>.Empty;
		
		public void SetMoves(int index, ImmutableHashSet<MoveSite> value) => FillMoves(index, 1, value);
		
		public void SetWrites(int index, ImmutableHashSet<SourceLocation> value) => FillWrites(index, 1, value);
		
		public void FillMoves(int offset, int count, ImmutableHashSet<MoveSite> value)
		{
			if (moves is null && value.IsEmpty)
				return;
			
			moves ??= Allocate<MoveSite>();
			moves.AsSpan(offset, count).Fill(value);
		}
		
		public void FillWrites(int offset, int count, ImmutableHashSet<SourceLocation> value)
		{
			if (writes is null && value.IsEmpty)
				return;
			
			writes ??= Allocate<SourceLocation>();
			writes.AsSpan(offset, count).Fill(value);
		}
		
		public Chunk Clone() => new((PathState[])States.Clone(), (ImmutableHashSet<MoveSite>[]?)moves?.Clone(),
			(ImmutableHashSet<SourceLocation>[]?)writes?.Clone());
		
		public static bool Covers(Chunk outer, Chunk inner, int length)
		{
			for (var i = 0; i < length; i++)
			{
				if ((outer.States[i] | inner.States[i]) != outer.States[i] ||
				    !Contains(outer.GetMoves(i), inner.GetMoves(i)) ||
				    !Contains(outer.GetWrites(i), inner.GetWrites(i)))
					return false;
			}
			
			return true;
		}
		
		private static ImmutableHashSet<T>[] Allocate<T>()
		{
			var sets = new ImmutableHashSet<T>[ChunkSize];
			Array.Fill(sets, ImmutableHashSet<T>.Empty);
			return sets;
		}
		
		private static bool Contains<T>(ImmutableHashSet<T> outer, ImmutableHashSet<T> inner) =>
			inner.IsEmpty || outer == inner || outer.IsSupersetOf(inner);
	}
}