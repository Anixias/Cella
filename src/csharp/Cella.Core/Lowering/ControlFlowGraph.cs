using System.Collections;
using System.Collections.Immutable;
using Cella.Core.Collections;

namespace Cella.Core.Lowering;

public sealed class ControlFlowGraph : IReadOnlyList<BasicBlock>
{
	public int Count => _blocks.Count;
	public BasicBlock this[int index] => _blocks[index];
	
	private static readonly ImmutableHashSet<BasicBlock> _emptyBlockSet = ImmutableHashSet<BasicBlock>.Empty;
	private readonly OrderedSet<BasicBlock> _blocks = [];
	private readonly Dictionary<BasicBlock, HashSet<BasicBlock>> _successors = [];
	private readonly Dictionary<BasicBlock, HashSet<BasicBlock>> _predecessors = [];
	
	public BasicBlock Add(BasicBlock block)
	{
		_blocks.Add(block);
		return block;
	}
	
	public bool Remove(BasicBlock block)
	{
		if (HasPredecessor(block))
			return false;
		
		if (!_blocks.Remove(block))
			return false;
		
		if (_successors.Remove(block, out var successors))
			foreach (var successor in successors)
				RemovePredecessor(successor, block);
		
		_predecessors.Remove(block);
		return true;
	}
	
	public void SetTerminator(BasicBlock? block, IBlockTerminator terminator)
	{
		if (block is null)
			return;
		
		// Allow lazily adding blocks, just in case
		_blocks.Add(block);
		
		// Remove block as a predecessor to other blocks
		switch (block.Terminator)
		{
			case BranchTerminator t:
				RemovePredecessor(t.Target, block);
				break;
			
			case ConditionalBranchTerminator t:
				RemovePredecessor(t.TrueTarget, block);
				RemovePredecessor(t.FalseTarget, block);
				break;
		}
		
		// Update terminator
		block.SetTerminatorRaw(terminator);
		
		// Clear existing successors/lazily add
		if (_successors.TryGetValue(block, out var successors))
		{
			successors.Clear();
		}
		else
		{
			successors = [];
			_successors.Add(block, successors);
		}
		
		// Add new successors
		switch (terminator)
		{
			case BranchTerminator t:
				successors.Add(t.Target);
				AddPredecessor(t.Target, block);
				break;
			
			case ConditionalBranchTerminator t:
				successors.Add(t.TrueTarget);
				successors.Add(t.FalseTarget);
				AddPredecessor(t.TrueTarget, block);
				AddPredecessor(t.FalseTarget, block);
				break;
			
			default:
				_successors.Remove(block);
				break;
		}
	}
	
	private void AddSuccessor(BasicBlock block, BasicBlock successor) =>
		_successors.GetOrAdd(block).Add(successor);
	
	private void RemoveSuccessor(BasicBlock block, BasicBlock successor)
	{
		if (!_successors.TryGetValue(block, out var successors))
			return;
		
		successors.Remove(successor);
		if (successors.Count == 0)
			_successors.Remove(block);
	}
	
	private void AddPredecessor(BasicBlock block, BasicBlock predecessor) =>
		_predecessors.GetOrAdd(block).Add(predecessor);
	
	private void RemovePredecessor(BasicBlock block, BasicBlock predecessor)
	{
		if (!_predecessors.TryGetValue(block, out var predecessors))
			return;
		
		predecessors.Remove(predecessor);
		if (predecessors.Count == 0)
			_predecessors.Remove(block);
	}
	
	public bool HasSuccessor(BasicBlock block) =>
		_successors.TryGetValue(block, out var successors) && successors.Count > 0;
	
	public bool HasPredecessor(BasicBlock block) =>
		_predecessors.TryGetValue(block, out var predecessors) && predecessors.Count > 0;
	
	public IReadOnlySet<BasicBlock> GetSuccessors(BasicBlock block) =>
		_successors.TryGetValue(block, out var successors) ? successors : _emptyBlockSet;
	
	public IReadOnlySet<BasicBlock> GetPredecessors(BasicBlock block) =>
		_predecessors.TryGetValue(block, out var predecessors) ? predecessors : _emptyBlockSet;
	
	public IEnumerator<BasicBlock> GetEnumerator() => _blocks.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	
	public HashSet<BasicBlock> FindReachable()
	{
		if (_blocks.Count == 0)
			return [];
		
		var reachable = new HashSet<BasicBlock>();
		var queue = new Queue<BasicBlock>();
		queue.Enqueue(_blocks[0]);
		
		while (queue.Count > 0)
		{
			var block = queue.Dequeue();
			if (!reachable.Add(block))
				continue;
			
			switch (block.Terminator)
			{
				case BranchTerminator t:
					queue.Enqueue(t.Target);
					break;
				
				case ConditionalBranchTerminator t:
					queue.Enqueue(t.TrueTarget);
					queue.Enqueue(t.FalseTarget);
					break;
			}
		}
		
		return reachable;
	}
}