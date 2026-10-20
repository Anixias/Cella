using System.Collections.Immutable;
using Cella.Core.Lowering;

namespace Cella.Core.Analysis;

public sealed class Loop(BasicBlock head, HashSet<BasicBlock> body)
{
	public BasicBlock Head { get; } = head;
	public HashSet<BasicBlock> Body { get; } = body;
	
	public bool HasExit => Body.Any(block => CfgUtils.GetSuccessors(block).Any(successor => !Body.Contains(successor)));
}

public readonly record struct LoopEdge(int Exit, int Back)
{
	public bool IsEmpty => Exit == 0 && Back == 0;
	
	public int Cross(int crossedLoop)
	{
		if (Exit > 0 && crossedLoop >= Exit)
			crossedLoop = 0;
		
		return Back == 0 ? crossedLoop : crossedLoop == 0 ? Back : Math.Min(crossedLoop, Back);
	}
	
	public ImmutableHashSet<MoveSite> Cross(ImmutableHashSet<MoveSite> sites)
	{
		var edge = this;
		return sites.IsEmpty
			? sites
			: [..sites.Select(site => site with { CrossedLoop = edge.Cross(site.CrossedLoop) })];
	}
	
	public ImmutableHashSet<WriteSite> Cross(ImmutableHashSet<WriteSite> sites)
	{
		var edge = this;
		return sites.IsEmpty
			? sites
			: [..sites.Select(site => site with { CrossedLoop = edge.Cross(site.CrossedLoop) })];
	}
}

public sealed class LoopEdges(IReadOnlyCollection<Loop> loops)
{
	private readonly Dictionary<Loop, int> _depths =
		loops.ToDictionary(static loop => loop, loop => loops.Count(other => other.Body.Contains(loop.Head)));
	
	private readonly Dictionary<(BasicBlock From, BasicBlock To), LoopEdge> _edges = [];
	
	public IReadOnlyCollection<Loop> Loops { get; } = loops;
	
	public int GetDepth(Loop loop) => _depths[loop];
	
	public LoopEdge Get(BasicBlock from, BasicBlock to)
	{
		if (_edges.TryGetValue((from, to), out var edge))
			return edge;
		
		var exits = _depths.Where(entry => entry.Key.Body.Contains(from) && !entry.Key.Body.Contains(to))
			.Select(static entry => entry.Value)
			.DefaultIfEmpty(0)
			.Min();
		
		var back = _depths.Where(entry => entry.Key.Head == to && entry.Key.Body.Contains(from))
			.Select(static entry => entry.Value)
			.DefaultIfEmpty(0)
			.Max();
		
		return _edges[(from, to)] = new LoopEdge(exits, back);
	}
}

public static class LoopForest
{
	public static List<Loop> Find(IReadOnlyList<BasicBlock> order)
	{
		var index = new Dictionary<BasicBlock, int>();
		for (var i = 0; i < order.Count; i++)
			index[order[i]] = i;
		
		var predecessors = order.ToDictionary(static block => block, static _ => new List<BasicBlock>());
		foreach (var block in order)
		{
			foreach (var successor in CfgUtils.GetSuccessors(block))
				predecessors[successor].Add(block);
		}
		
		var dominators = FindDominators(order, index, predecessors);
		var bodies = new Dictionary<BasicBlock, HashSet<BasicBlock>>();
		foreach (var block in order)
		{
			foreach (var head in CfgUtils.GetSuccessors(block).Where(head => Dominates(head, block, dominators, index)))
			{
				if (!bodies.TryGetValue(head, out var body))
					bodies[head] = body = [head];
				
				var pending = new Stack<BasicBlock>();
				if (body.Add(block))
					pending.Push(block);
				
				while (pending.Count > 0)
				{
					foreach (var predecessor in predecessors[pending.Pop()])
					{
						if (body.Add(predecessor))
							pending.Push(predecessor);
					}
				}
			}
		}
		
		return bodies.Select(static entry => new Loop(entry.Key, entry.Value)).ToList();
	}
	
	private static BasicBlock?[] FindDominators(IReadOnlyList<BasicBlock> order, Dictionary<BasicBlock, int> index,
		Dictionary<BasicBlock, List<BasicBlock>> predecessors)
	{
		var dominators = new BasicBlock?[order.Count];
		if (order.Count == 0)
			return dominators;
		
		dominators[0] = order[0];
		var changed = true;
		while (changed)
		{
			changed = false;
			for (var i = 1; i < order.Count; i++)
			{
				BasicBlock? dominator = null;
				foreach (var predecessor in predecessors[order[i]])
				{
					if (dominators[index[predecessor]] is null)
						continue;
					
					dominator = dominator is null ? predecessor : Intersect(predecessor, dominator, dominators, index);
				}
				
				if (dominator is null || dominators[i] == dominator)
					continue;
				
				dominators[i] = dominator;
				changed = true;
			}
		}
		
		return dominators;
	}
	
	private static BasicBlock Intersect(BasicBlock first, BasicBlock second, BasicBlock?[] dominators,
		Dictionary<BasicBlock, int> index)
	{
		while (first != second)
		{
			while (index[first] > index[second])
				first = dominators[index[first]]!;
			
			while (index[second] > index[first])
				second = dominators[index[second]]!;
		}
		
		return first;
	}
	
	private static bool Dominates(BasicBlock dominator, BasicBlock block, BasicBlock?[] dominators,
		Dictionary<BasicBlock, int> index)
	{
		while (true)
		{
			if (block == dominator)
				return true;
			
			var parent = dominators[index[block]];
			if (parent is null || parent == block)
				return false;
			
			block = parent;
		}
	}
}