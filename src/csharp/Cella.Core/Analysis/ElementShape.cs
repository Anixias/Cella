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
		ElementsShape elements => elements.Parts.Aggregate(PathState.None,
			static (state, part) => state | Summarize(part)),
		_ => PathState.None
	};
	
	public static bool HasInitializedPart(Shape shape) => shape switch
	{
		LeafShape leaf => leaf.State == PathState.Initialized,
		FieldsShape fields => fields.Parts.Any(HasInitializedPart),
		ElementsShape elements => elements.Parts.Any(HasInitializedPart),
		_ => false
	};
	
	public static bool IsWhollyInitialized(Shape shape) => Summarize(shape) == PathState.Initialized;
	
	public static bool IsUninitialized(Shape shape) => !Summarize(shape).HasFlag(PathState.Initialized);
	
	public static ImmutableHashSet<MoveSite> GetMoves(Shape shape) => shape switch
	{
		LeafShape leaf => leaf.Moves,
		FieldsShape fields => fields.Parts.Aggregate(ImmutableHashSet<MoveSite>.Empty,
			static (moves, part) => Union(moves, GetMoves(part))),
		ElementsShape elements => elements.Parts.Aggregate(ImmutableHashSet<MoveSite>.Empty,
			static (moves, part) => Union(moves, GetMoves(part))),
		_ => []
	};
	
	public static Shape MapLeaves(Shape shape, Func<LeafShape, LeafShape> map) => shape switch
	{
		LeafShape leaf => map(leaf),
		FieldsShape fields => FieldsShape.Create([..fields.Parts.Select(part => MapLeaves(part, map))]),
		ElementsShape elements => ElementsShape.Create(
			elements.Runs.Select(run => run.Map(part => MapLeaves(part, map))), elements.Length),
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

public readonly record struct ElementRun(BigInteger Start, ImmutableArray<Shape> Cycle)
{
	public ElementRun(BigInteger start, Shape shape) : this(start, [shape])
	{
	}
	
	public bool IsRepeating => Cycle.Length > 1;
	
	public Shape GetAt(BigInteger index) => Cycle[(int)BigInteger.Remainder(index - Start, Cycle.Length)];
	
	public ElementRun From(BigInteger start)
	{
		var run = this;
		return new(start, [..Enumerable.Range(0, Cycle.Length).Select(k => run.GetAt(start + k))]);
	}
	
	public ElementRun Map(Func<Shape, Shape> map) => this with { Cycle = [..Cycle.Select(map)] };
}

public sealed class ElementsShape : Shape
{
	private const int CombinedLength = 16;
	
	private ElementsShape(ImmutableArray<ElementRun> runs, BigInteger length)
	{
		Runs = runs;
		Length = length;
	}
	
	public ImmutableArray<ElementRun> Runs { get; }
	public BigInteger Length { get; }
	
	public IEnumerable<Shape> Parts => Runs.SelectMany(static run => run.Cycle);
	
	public static Shape Create(IEnumerable<ElementRun> runs, BigInteger length)
	{
		var pieces = runs.ToList();
		var merged = new List<ElementRun>(pieces.Count);
		for (var i = 0; i < pieces.Count; i++)
		{
			var end = i + 1 < pieces.Count ? pieces[i + 1].Start : length;
			if (end <= pieces[i].Start)
				continue;
			
			var run = Shorten(pieces[i], end - pieces[i].Start);
			if (merged.Count == 0)
			{
				merged.Add(run);
				continue;
			}
			
			var previous = merged[^1];
			if (Continues(previous, run, end))
				continue;
			
			if (end - previous.Start <= CombinedLength)
				merged[^1] = Shorten(new ElementRun(previous.Start,
						[..Enumerate(previous, previous.Start, run.Start), ..Enumerate(run, run.Start, end)]),
					end - previous.Start);
			else
				merged.Add(run);
		}
		
		return merged is [{ Cycle: [LeafShape leaf] }] ? leaf : new ElementsShape([..merged], length);
	}
	
	private static IEnumerable<Shape> Enumerate(ElementRun run, BigInteger start, BigInteger end)
	{
		for (var index = start; index < end; index++)
			yield return run.GetAt(index);
	}
	
	private static ElementRun Shorten(ElementRun run, BigInteger length)
	{
		var cycle = length < run.Cycle.Length ? run.Cycle[..(int)length] : run.Cycle;
		for (var period = 1; period < cycle.Length; period++)
		{
			if ((length > cycle.Length && cycle.Length % period != 0) ||
			    Enumerable.Range(period, cycle.Length - period).Any(k => !cycle[k].Equals(cycle[k % period])))
				continue;
			
			cycle = cycle[..period];
			break;
		}
		
		return run with { Cycle = cycle };
	}
	
	private static bool Continues(ElementRun previous, ElementRun next, BigInteger end)
	{
		var count = BigInteger.Min(end - next.Start, Lcm(previous.Cycle.Length, next.Cycle.Length));
		for (var k = BigInteger.Zero; k < count; k++)
		{
			if (!next.GetAt(next.Start + k).Equals(previous.GetAt(next.Start + k)))
				return false;
		}
		
		return true;
	}
	
	private static BigInteger Lcm(BigInteger a, BigInteger b) => a / BigInteger.GreatestCommonDivisor(a, b) * b;
	
	public static ElementsShape Expand(Shape shape, BigInteger length) =>
		shape as ElementsShape ?? new ElementsShape([new(BigInteger.Zero, shape)], length);
	
	public BigInteger GetEnd(int run) => run + 1 < Runs.Length ? Runs[run + 1].Start : Length;
	
	private int FindRun(BigInteger index)
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
		
		return low;
	}
	
	public Shape Get(BigInteger index) => Runs[FindRun(index)].GetAt(index);
	
	public Shape Set(BigInteger index, Shape shape) => SetRange(index, index + 1, shape);
	
	public Shape SetRange(BigInteger start, BigInteger end, Shape shape) =>
		SetStrided(start, end, BigInteger.One, shape);
	
	public Shape SetStrided(BigInteger start, BigInteger end, BigInteger stride, Shape shape)
	{
		if (start >= end)
			return this;
		
		var pieces = new List<ElementRun>(Runs.Length + 2);
		for (var i = 0; i < Runs.Length; i++)
		{
			var run = Runs[i];
			var runEnd = GetEnd(i);
			if (runEnd <= start || run.Start >= end)
			{
				pieces.Add(run);
				continue;
			}
			
			if (run.Start < start)
				pieces.Add(run);
			
			var from = BigInteger.Max(run.Start, start);
			var count = BigInteger.Min(Lcm(run.Cycle.Length, stride), BigInteger.Min(runEnd, end) - from);
			pieces.Add(new ElementRun(from,
			[
				..Enumerate(run, from, from + count).Select((part, k) =>
					BigInteger.Remainder(from + k - start, stride).IsZero ? shape : part)
			]));
			
			if (runEnd > end)
				pieces.Add(run.From(end));
		}
		
		return Create(pieces, Length);
	}
	
	public bool IsUniform(BigInteger start, BigInteger end, Shape shape) =>
		IsUniform(start, end, BigInteger.One, shape);
	
	public bool IsUniform(BigInteger start, BigInteger end, BigInteger stride, Shape shape)
	{
		for (var i = 0; i < Runs.Length && start < end; i++)
		{
			var run = Runs[i];
			var runEnd = GetEnd(i);
			if (runEnd <= start || run.Start >= end)
				continue;
			
			var first = start + BigInteger.Max(BigInteger.Zero, run.Start - start + stride - 1) / stride * stride;
			var last = BigInteger.Min(runEnd, end);
			var count = BigInteger.Min(run.Cycle.Length, (last - first + stride - 1) / stride);
			for (var k = BigInteger.Zero; k < count; k++)
			{
				if (!run.GetAt(first + k * stride).Equals(shape))
					return false;
			}
		}
		
		return true;
	}
	
	public static Shape Join(ElementsShape first, ElementsShape second) => Create(
		Pair(first, second).Select(static segment =>
			new ElementRun(segment.Start, [..segment.First.Zip(segment.Second, Shape.Join)])), first.Length);
	
	private static IEnumerable<(BigInteger Start, ImmutableArray<Shape> First, ImmutableArray<Shape> Second)> Pair(
		ElementsShape first, ElementsShape second)
	{
		var starts = first.Runs.Select(static run => run.Start)
			.Concat(second.Runs.Select(static run => run.Start))
			.Distinct()
			.Order()
			.ToList();
		
		for (var i = 0; i < starts.Count; i++)
		{
			var start = starts[i];
			var end = i + 1 < starts.Count ? starts[i + 1] : first.Length;
			var a = first.Runs[first.FindRun(start)];
			var b = second.Runs[second.FindRun(start)];
			var count = BigInteger.Min(Lcm(a.Cycle.Length, b.Cycle.Length), end - start);
			yield return (start, [..Enumerate(a, start, start + count)], [..Enumerate(b, start, start + count)]);
		}
	}
	
	public override bool Equals(Shape? other) =>
		ReferenceEquals(this, other) || other is ElementsShape elements && Length == elements.Length &&
		Pair(this, elements).All(static segment => segment.First.SequenceEqual(segment.Second));
	
	public override int GetHashCode() => Length.GetHashCode();
}