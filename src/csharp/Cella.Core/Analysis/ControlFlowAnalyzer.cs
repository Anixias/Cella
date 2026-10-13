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
		
		if (!CanFallOffEnd(function))
			return true;
		
		diagnostics.Add(new(DiagnosticSeverity.Error, function.Info.Symbol.Definition, returnType is NeverType
			? $"{DiagnosticReporter.DescribeFunction(function.Info.Symbol, true)} can reach its end"
			: "Not all paths return a value"));
		
		return false;
	}
	
	private static bool CanFallOffEnd(LoweredFunction function)
	{
		if (function.Blocks.Count == 0)
			return false;
		
		var visited = new HashSet<BasicBlock> { function.Blocks[0] };
		var pending = new Stack<BasicBlock>(visited);
		while (pending.TryPop(out var block))
		{
			if (block.Terminator is UndefinedTerminator)
				return true;
			
			foreach (var target in GetTargets(block.Terminator))
				if (visited.Add(target))
					pending.Push(target);
		}
		
		return false;
	}
	
	private static IEnumerable<BasicBlock> GetTargets(IBlockTerminator terminator) =>
		GetOnlyTarget(terminator) is { } only
			? [only]
			: terminator is ConditionalBranchTerminator branch
				? [branch.TrueTarget, branch.FalseTarget]
				: [];
	
	private static BasicBlock? GetOnlyTarget(IBlockTerminator terminator) => terminator switch
	{
		BranchTerminator branch => branch.Target,
		ConditionalBranchTerminator { Condition: ConstantValue { Value: bool taken } } branch =>
			taken ? branch.TrueTarget : branch.FalseTarget,
		_ => null
	};
}