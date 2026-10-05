using Cella.Core.Lowering;

namespace Cella.Core.Analysis;

public static class CfgUtils
{
	public static BasicBlock[] GetSuccessors(BasicBlock block) => block.Terminator switch
	{
		BranchTerminator t => [t.Target],
		ConditionalBranchTerminator t => [t.TrueTarget, t.FalseTarget],
		_ => Array.Empty<BasicBlock>()
	};
	
	public static Dictionary<BasicBlock, List<BasicBlock>> ComputePredecessors(LoweredFunction function)
	{
		var predecessors = new Dictionary<BasicBlock, List<BasicBlock>>(function.Blocks.Count);
		
		foreach (var block in function.Blocks)
			foreach (var successor in GetSuccessors(block))
				predecessors.GetOrAdd(successor).Add(block);
		
		return predecessors;
	}
	
	public static HashSet<BasicBlock> FindReachableBlocks(LoweredFunction function)
	{
		if (function.Blocks.Count == 0)
			return [];
		
		var reachable = new HashSet<BasicBlock>();
		var queue = new Queue<BasicBlock>();
		queue.Enqueue(function.Blocks[0]);
		
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
	
	public static List<BasicBlock> GetReversePostorder(LoweredFunction function)
	{
		var order = new List<BasicBlock>(function.Blocks.Count);
		if (function.Blocks.Count == 0)
			return order;
		
		var entry = function.Blocks[0];
		var visited = new HashSet<BasicBlock> { entry };
		var stack = new Stack<(BasicBlock Block, int Next)>();
		stack.Push((entry, 0));
		while (stack.Count > 0)
		{
			var (block, next) = stack.Pop();
			var successors = GetSuccessors(block);
			if (next == successors.Length)
			{
				order.Add(block);
				continue;
			}
			
			stack.Push((block, next + 1));
			if (visited.Add(successors[next]))
				stack.Push((successors[next], 0));
		}
		
		order.Reverse();
		return order;
	}
}