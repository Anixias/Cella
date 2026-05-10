using Cella.Core.Collections;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Diagnostics;

namespace Cella.Core.Analysis;

public sealed class ControlFlowAnalyzer(DiagnosticList diagnostics)
{
	public bool Analyze(LoweredFunction function)
	{
		DetectUnreachableCode(function);
		return AllPathsReturn(function);
	}
	
	private void DetectUnreachableCode(LoweredFunction function)
	{
		var reachableBlocks = function.Blocks.FindReachable();
		var unreachableRegion = false;
		
		foreach (var block in function.Blocks)
		{
			if (reachableBlocks.Contains(block))
			{
				unreachableRegion = false;
				continue;
			}
			
			if (unreachableRegion)
				continue;
			
			unreachableRegion = true;
			var (source, range) = block.GetSourceLocation();
			range = new(range.Start, range.Start); // Empty length to avoid coloring first character only
			diagnostics.Add(DiagnosticReporter.ReportUnreachableCode(new(source, range)));
		}
	}
	
	private bool AllPathsReturn(LoweredFunction function)
	{
		if (function.Info.Signature.ReturnType == NativeSymbols.Void)
			return true;
		
		var allPathsReturn = true;
		foreach (var path in GetMinimalPaths(function))
		{
			if (path.Type is not (BlockPathType.Undefined or BlockPathType.ReturnsVoid))
				continue;
			
			allPathsReturn = false;
			break;
		}
		
		if (allPathsReturn)
			return true;
		
		diagnostics.Add(new(DiagnosticSeverity.Error, function.Info.Symbol.Definition,
			"Not all paths return a value!"));
		
		return false;
	}
	
	private enum BlockPathType
	{
		Undefined,
		ReturnsVoid,
		ReturnsValue,
		Loops
	}
	
	private readonly record struct BlockPath(BlockPathType Type, OrderedSet<BasicBlock> Path);
	
	/// <summary>
	/// Returns all minimal paths through a function body. Minimal means that the paths returned will not loop. If a
	/// returned path has type <see cref="BlockPathType.Loops"/>, it ends at the block that would have then
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
		while (true)
		{
			switch (current.Terminator)
			{
				case UndefinedTerminator:
					yield return new(BlockPathType.Undefined, path);
					yield break;
				
				case ReturnTerminator t:
					yield return new(t.Value is null ? BlockPathType.ReturnsVoid : BlockPathType.ReturnsValue, path);
					yield break;
				
				case BranchTerminator term:
					var target = term.Target;
					
					// Loop
					if (!path.Add(target))
					{
						yield return new(BlockPathType.Loops, path);
						yield break;
					}
					
					current = target;
					continue;
				
				case ConditionalBranchTerminator term:
					var trueTarget = term.TrueTarget;
					var falseTarget = term.FalseTarget;
					var trueLoops = path.Contains(trueTarget);
					var falseLoops = path.Contains(falseTarget);
					
					// Infinite loop
					if (trueLoops && falseLoops)
					{
						yield return new(BlockPathType.Loops, path);
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
						
						current = falseTarget;
						path = newPath;
						continue;
					}
					
					yield break;
			}
			
			break;
		}
	}
}