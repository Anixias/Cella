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
		var reachableBlocks = FindLiveBlocks(function);
		var unreachable = function.Statements.Where(statement => !reachableBlocks.Contains(statement.Block))
			.ToHashSet();
		
		var next = function.Statements.Where(static statement => statement.Previous is not null)
			.ToDictionary(static statement => statement.Previous!);
		
		foreach (var statement in function.Statements)
		{
			if (!unreachable.Contains(statement) || statement.Parent is { } parent && unreachable.Contains(parent) ||
			    statement.Previous is { } previous && unreachable.Contains(previous))
				continue;
			
			var (source, range) = statement.Location;
			var end = range.End;
			for (var following = statement;
			     next.TryGetValue(following, out var sibling) && unreachable.Contains(sibling);
			     following = sibling)
				end = Math.Max(end, sibling.Location.Range.End);
			
			diagnostics.Add(DiagnosticReporter.ReportUnreachableCode(new(source, new(range.Start, range.Start))) with
			{
				UnnecessaryLocation = new(source, new(range.Start, end))
			});
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
	
	private static bool CanFallOffEnd(LoweredFunction function) =>
		FindLiveBlocks(function).Any(static block => block.Terminator is UndefinedTerminator);
	
	private static HashSet<BasicBlock> FindLiveBlocks(LoweredFunction function)
	{
		if (function.Blocks.Count == 0)
			return [];
		
		var visited = new HashSet<BasicBlock> { function.Blocks[0] };
		var pending = new Stack<BasicBlock>(visited);
		while (pending.TryPop(out var block))
		{
			foreach (var target in GetTargets(block.Terminator))
				if (visited.Add(target))
					pending.Push(target);
		}
		
		return visited;
	}
	
	private static IEnumerable<BasicBlock> GetTargets(IBlockTerminator terminator) =>
		GetOnlyTarget(terminator) is { } only
			? [only]
			: terminator switch
			{
				ConditionalBranchTerminator branch => [branch.TrueTarget, branch.FalseTarget],
				SwitchTerminator branch => branch.Targets,
				_ => []
			};
	
	private static BasicBlock? GetOnlyTarget(IBlockTerminator terminator) => terminator switch
	{
		BranchTerminator branch => branch.Target,
		ConditionalBranchTerminator { Condition: ConstantValue { Value: bool taken } } branch =>
			taken ? branch.TrueTarget : branch.FalseTarget,
		_ => null
	};
}