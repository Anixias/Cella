using Cella.Core.Lowering;

namespace Cella.Core.Analysis;

public static class CfgUtils
{
	public static BasicBlock[] GetSuccessors(BasicBlock block) => block.Terminator switch
	{
		BranchTerminator t => [t.Target],
		ConditionalBranchTerminator { IsErasedCall: false, Condition: ConstantValue { Value: bool taken } } t =>
			[taken ? t.TrueTarget : t.FalseTarget],
		ConditionalBranchTerminator t => [t.TrueTarget, t.FalseTarget],
		SwitchTerminator t => t.Targets,
		_ => Array.Empty<BasicBlock>()
	};
	
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