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
		var returnType = function.Info.Signature.ReturnType;
		if (returnType == NativeSymbols.Void)
			return true;
		
		if (GetMinimalPaths(function).All(static path => path is not BlockPathType.Undefined))
			return true;
		
		diagnostics.Add(new(DiagnosticSeverity.Error, function.Info.Symbol.Definition, returnType is NeverType
			? $"'{function.Info.Symbol.Name}' can reach its end"
			: "Not all paths return a value"));
		
		return false;
	}
	
	private enum BlockPathType
	{
		Undefined,
		Returns,
		Stops,
		Loops
	}
	
	/// <summary>
	/// Returns how each minimal path through a function body ends. Minimal means that the paths will not loop.
	/// </summary>
	private static IEnumerable<BlockPathType> GetMinimalPaths(LoweredFunction function)
	{
		if (function.Blocks.Count == 0)
			yield break;
		
		var entry = function.Blocks[0];
		var initialPath = new OrderedSet<BasicBlock> { entry };
		foreach (var path in Explore(entry, initialPath))
			yield return path;
	}
	
	private static IEnumerable<BlockPathType> Explore(BasicBlock current, OrderedSet<BasicBlock> path)
	{
		while (true)
		{
			if (GetOnlyTarget(current.Terminator) is { } target)
			{
				// Loop
				if (!path.Add(target))
				{
					yield return BlockPathType.Loops;
					yield break;
				}
				
				current = target;
				continue;
			}
			
			switch (current.Terminator)
			{
				case UndefinedTerminator:
					yield return BlockPathType.Undefined;
					yield break;
				
				case ReturnTerminator:
					yield return BlockPathType.Returns;
					yield break;
				
				case UnreachableTerminator:
					yield return BlockPathType.Stops;
					yield break;
				
				case ConditionalBranchTerminator term:
					var trueTarget = term.TrueTarget;
					var falseTarget = term.FalseTarget;
					var trueLoops = path.Contains(trueTarget);
					var falseLoops = path.Contains(falseTarget);
					
					// Infinite loop
					if (trueLoops && falseLoops)
					{
						yield return BlockPathType.Loops;
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
	
	private static BasicBlock? GetOnlyTarget(IBlockTerminator terminator) => terminator switch
	{
		BranchTerminator branch => branch.Target,
		ConditionalBranchTerminator { Condition: ConstantValue { Value: bool taken } } branch =>
			taken ? branch.TrueTarget : branch.FalseTarget,
		_ => null
	};
}