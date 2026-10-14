using System.Collections.Immutable;
using System.Numerics;

namespace Cella.Core.Analysis;

public abstract class Shape : IEquatable<Shape>
{
	public abstract bool Equals(Shape? other);
	
	public override bool Equals(object? obj) => obj is Shape other && Equals(other);
	
	public abstract override int GetHashCode();
	
	public static PathState Summarize(Shape shape) => shape switch
	{
		LeafShape leaf => leaf.State,
		FieldsShape fields => fields.Parts.Aggregate(PathState.None, static (state, part) => state | Summarize(part)),
		ElementsShape elements => elements.Runs.Aggregate(PathState.None,
			static (state, run) => state | Summarize(run.Shape)),
		_ => PathState.None
	};
	
	public static bool HasInitializedPart(Shape shape) => shape switch
	{
		LeafShape leaf => leaf.State == PathState.Initialized,
		FieldsShape fields => fields.Parts.Any(HasInitializedPart),
		ElementsShape elements => elements.Runs.Any(static run => HasInitializedPart(run.Shape)),
		_ => false
	};
	
	public static bool IsWhollyInitialized(Shape shape) => Summarize(shape) == PathState.Initialized;
	
	public static bool IsUninitialized(Shape shape) => !Summarize(shape).HasFlag(PathState.Initialized);
	
	public static ImmutableHashSet<MoveSite> GetMoves(Shape shape) => shape switch
	{
		LeafShape leaf => leaf.Moves,
		FieldsShape fields => fields.Parts.Aggregate(ImmutableHashSet<MoveSite>.Empty,
			static (moves, part) => Union(moves, GetMoves(part))),
		ElementsShape elements => elements.Runs.Aggregate(ImmutableHashSet<MoveSite>.Empty,
			static (moves, run) => Union(moves, GetMoves(run.Shape))),
		_ => []
	};
	
	public static Shape MapLeaves(Shape shape, Func<LeafShape, LeafShape> map) => shape switch
	{
		LeafShape leaf => map(leaf),
		FieldsShape fields => FieldsShape.Create([..fields.Parts.Select(part => MapLeaves(part, map))]),
		ElementsShape elements => ElementsShape.Create(
			elements.Runs.Select(run => run with { Shape = MapLeaves(run.Shape, map) }), elements.Length),
		_ => shape
	};
	
	public static Shape Join(Shape first, Shape second)
	{
		if (first.Equals(second))
			return first;
		
		switch (first, second)
		{
			case (LeafShape a, LeafShape b):
				return new LeafShape(a.State | b.State, Union(a.Moves, b.Moves));
			
			case (FieldsShape a, _):
				return FieldsShape.Create([..a.Parts.Select((part, i) => Join(part, FieldsShape.GetPart(second, i)))]);
			
			case (_, FieldsShape b):
				return FieldsShape.Create([..b.Parts.Select((part, i) => Join(FieldsShape.GetPart(first, i), part))]);
			
			case (ElementsShape a, _):
				return ElementsShape.Join(a, ElementsShape.Expand(second, a.Length));
			
			case (_, ElementsShape b):
				return ElementsShape.Join(ElementsShape.Expand(first, b.Length), b);
			
			default:
				return new LeafShape(Summarize(first) | Summarize(second), Union(GetMoves(first), GetMoves(second)));
		}
	}
	
	public static ImmutableHashSet<T> Union<T>(ImmutableHashSet<T> first, ImmutableHashSet<T> second) =>
		second.IsEmpty || first == second ? first : first.IsEmpty ? second : first.Union(second);
}

public sealed class LeafShape(PathState state, ImmutableHashSet<MoveSite> moves) : Shape
{
	public PathState State { get; } = state;
	public ImmutableHashSet<MoveSite> Moves { get; } = moves;
	
	public override bool Equals(Shape? other) => other is LeafShape leaf && leaf.State == State &&
	                                            (leaf.Moves == Moves || leaf.Moves.SetEquals(Moves));
	
	public override int GetHashCode() => HashCode.Combine(State, Moves.Count);
}

public sealed class FieldsShape : Shape
{
	private FieldsShape(ImmutableArray<Shape> parts)
	{
		Parts = parts;
	}
	
	public ImmutableArray<Shape> Parts { get; }
	
	public static Shape Create(ImmutableArray<Shape> parts) =>
		parts.Length > 0 && parts[0] is LeafShape first && parts.All(first.Equals) ? first : new FieldsShape(parts);
	
	public static Shape GetPart(Shape shape, int index) => shape is FieldsShape fields ? fields.Parts[index] : shape;
	
	public override bool Equals(Shape? other) => other is FieldsShape fields && Parts.SequenceEqual(fields.Parts);
	
	public override int GetHashCode() => Parts.Length;
}

public readonly record struct ElementRun(BigInteger Start, Shape Shape);

public sealed class ElementsShape : Shape
{
	private ElementsShape(ImmutableArray<ElementRun> runs, BigInteger length)
	{
		Runs = runs;
		Length = length;
	}
	
	public ImmutableArray<ElementRun> Runs { get; }
	public BigInteger Length { get; }
	
	public static Shape Create(IEnumerable<ElementRun> runs, BigInteger length)
	{
		var merged = ImmutableArray.CreateBuilder<ElementRun>();
		foreach (var run in runs)
		{
			if (merged.Count > 0 && merged[^1].Shape.Equals(run.Shape))
				continue;
			
			merged.Add(run);
		}
		
		return merged is [{ Shape: LeafShape leaf }] ? leaf : new ElementsShape(merged.ToImmutable(), length);
	}
	
	public static ElementsShape Expand(Shape shape, BigInteger length) =>
		shape as ElementsShape ?? new ElementsShape([new(BigInteger.Zero, shape)], length);
	
	public BigInteger GetEnd(int run) => run + 1 < Runs.Length ? Runs[run + 1].Start : Length;
	
	public Shape Get(BigInteger index)
	{
		var low = 0;
		var high = Runs.Length - 1;
		while (low < high)
		{
			var middle = (low + high + 1) / 2;
			if (Runs[middle].Start <= index)
				low = middle;
			else
				high = middle - 1;
		}
		
		return Runs[low].Shape;
	}
	
	public Shape Set(BigInteger index, Shape shape) => SetRange(index, index + 1, shape);
	
	public Shape SetRange(BigInteger start, BigInteger end, Shape shape)
	{
		if (start >= end)
			return this;
		
		var pieces = new List<ElementRun>(Runs.Length + 2);
		for (var i = 0; i < Runs.Length; i++)
		{
			var runStart = Runs[i].Start;
			var runEnd = GetEnd(i);
			if (runEnd <= start || runStart >= end)
			{
				pieces.Add(Runs[i]);
				continue;
			}
			
			if (runStart < start)
				pieces.Add(Runs[i]);
			
			if (runStart <= start)
				pieces.Add(new(start, shape));
			
			if (runEnd > end)
				pieces.Add(new(end, Runs[i].Shape));
		}
		
		return Create(pieces, Length);
	}
	
	public bool IsUniform(BigInteger start, BigInteger end, Shape shape)
	{
		for (var i = 0; i < Runs.Length; i++)
		{
			if (GetEnd(i) > start && Runs[i].Start < end && !Runs[i].Shape.Equals(shape))
				return false;
		}
		
		return true;
	}
	
	public static Shape Join(ElementsShape first, ElementsShape second)
	{
		var starts = first.Runs.Select(static run => run.Start)
			.Concat(second.Runs.Select(static run => run.Start))
			.Distinct()
			.Order();
		
		return Create(starts.Select(start => new ElementRun(start, Shape.Join(first.Get(start), second.Get(start)))),
			first.Length);
	}
	
	public override bool Equals(Shape? other) =>
		other is ElementsShape elements && Length == elements.Length && Runs.SequenceEqual(elements.Runs);
	
	public override int GetHashCode() => HashCode.Combine(Runs.Length, Length);
}