using Cella.Core.Collections;
using Cella.Core.Lowering;

namespace Cella.Core.Analysis;

public sealed class ControlFlowAnalyzer
{
	public IReadOnlyList<string> Diagnostics => _diagnostics;
	
	private readonly List<string> _diagnostics = [];
	
	public void Analyze(LoweredFunction function)
	{
		var allPathsReturn = true;
		foreach (var path in GetMinimalPaths(function))
		{
			switch (path.Type)
			{
				case BlockPathType.Undefined:
					if (function.Symbol.ReturnType is not null)
					{
						// TODO Implicit void return but we require a return type, error!
						allPathsReturn = false;
					}
					break;
			}
		}
		
		// TEMP
		if (!allPathsReturn)
			_diagnostics.Add("Not all paths return a value!");
	}
	
	private enum BlockPathType
	{
		Undefined,
		Returns,
		InfiniteLoop
	}
	
	private readonly record struct BlockPath(BlockPathType Type, OrderedSet<BasicBlock> Path);
	
	/// <summary>
	/// Returns all minimal paths through a function body. Minimal means that the paths returned will not loop. If a
	/// returned path has type <see cref="BlockPathType.InfiniteLoop"/>, it ends at the block that would have then
	/// looped back to a previously-visited block. It will only contain distinct blocks.
	/// </summary>
	private static IEnumerable<BlockPath> GetMinimalPaths(LoweredFunction function)
	{
		if (function.Blocks.Count == 0)
			yield break;
		
		var entry = function.Blocks[0];
		var initialPath = new OrderedSet<BasicBlock> { entry };
		foreach (var path in Explore(entry, initialPath))
			yield return path;
	}
	
	private static IEnumerable<BlockPath> Explore(BasicBlock current, OrderedSet<BasicBlock> path)
	{
		switch (current.Terminator)
		{
			case UndefinedTerminator:
				yield return new(BlockPathType.Undefined, path);
				yield break;
			
			case ReturnTerminator:
				yield return new(BlockPathType.Returns, path);
				yield break;
			
			case BranchTerminator term:
				var target = term.Target;
				
				// Infinite loop
				if (!path.Add(target))
				{
					yield return new(BlockPathType.InfiniteLoop, path);
					yield break;
				}
				
				foreach (var subpath in Explore(target, path))
					yield return subpath;
				
				yield break;
			
			case ConditionalBranchTerminator term:
				var trueTarget = term.TrueTarget;
				var falseTarget = term.FalseTarget;
				var trueLoops = path.Contains(trueTarget);
				var falseLoops = path.Contains(falseTarget);
				
				// Infinite loop
				if (trueLoops && falseLoops)
				{
					yield return new(BlockPathType.InfiniteLoop, path);
					yield break;
				}
				
				// If only one branch is going to be explored, we allow mutation of the path parameter
				var allowModification = trueLoops != falseLoops;
				
				if (!trueLoops)
				{
					var newPath = allowModification
						? path
						: new OrderedSet<BasicBlock>(path);
					
					newPath.Add(trueTarget);
					
					foreach (var subpath in Explore(trueTarget, newPath))
						yield return subpath;
				}
				
				if (!falseLoops)
				{
					var newPath = allowModification
						? path
						: new OrderedSet<BasicBlock>(path);
					
					newPath.Add(falseTarget);
					
					foreach (var subpath in Explore(falseTarget, newPath))
						yield return subpath;
				}
				
				yield break;
		}
	}
}