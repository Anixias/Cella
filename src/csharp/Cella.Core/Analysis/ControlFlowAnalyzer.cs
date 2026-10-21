using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Analysis;

public sealed class ControlFlowAnalyzer(DiagnosticList diagnostics)
{
	public bool Analyze(LoweredFunction function)
	{
		DetectUnreachableCode(function);
		DetectEndlessRecursion(function);
		return AllPathsReturn(function);
	}
	
	private void DetectEndlessRecursion(LoweredFunction function)
	{
		var self = function.Info.Symbol;
		if (function.Blocks.Count == 0 || self.IsConversion)
			return;
		
		var recurses = false;
		var visited = new HashSet<BasicBlock> { function.Blocks[0] };
		var pending = new Stack<BasicBlock>(visited);
		while (pending.TryPop(out var block))
		{
			if (GetValues(block).Any(value => CallsFunction(value, self)))
			{
				recurses = true;
				continue;
			}
			
			var targets = GetTargets(block.Terminator).ToList();
			if (targets.Count == 0)
				return;
			
			foreach (var target in targets)
				if (visited.Add(target))
					pending.Push(target);
		}
		
		if (recurses)
			diagnostics.Add(new(DiagnosticSeverity.Warning, self.Definition,
				$"{DiagnosticReporter.DescribeFunction(self, true)} recurses forever")
			{
				Lint = Lints.EndlessRecursion
			});
	}
	
	private static IEnumerable<Value> GetValues(BasicBlock block) => block.Instructions
		.Select(static instruction => instruction switch
		{
			LocalVarInstruction local => local.Initializer,
			ExpressionInstruction expression => expression.Value,
			_ => null
		})
		.Append(block.Terminator switch
		{
			ReturnTerminator ret => ret.Value,
			ConditionalBranchTerminator branch => branch.Condition,
			SwitchTerminator branch => branch.Value,
			_ => null
		})
		.OfType<Value>();
	
	private static bool CallsFunction(Value value, FunctionSymbol function) =>
		value is CallValue call && call.Function.Symbol == function ||
		GetOperands(value).OfType<Value>().Any(operand => CallsFunction(operand, function));
	
	private static IEnumerable<Value?> GetOperands(Value value) => value switch
	{
		MoveValue move => [move.Place],
		ConversionValue conversion => [conversion.Source],
		CallValue call => [..call.Arguments],
		IndirectCallValue call => [call.Target, ..call.Arguments],
		ClosureValue closure => [..closure.Captures],
		OwnClosureValue closure => [..closure.Captures],
		IndexerValue indexer => [indexer.Target, indexer.Index],
		AccessValue access => [access.Target],
		EnumValue enumValue => [..enumValue.Payload],
		EnumTagValue tag => [tag.Target],
		EnumPayloadValue payload => [payload.Target],
		ArrayValue array => [..array.Elements],
		FStrValue fstr => [fstr.Text, fstr.Values],
		FStrPartValue part => [part.Target, part.Index],
		AtomicValue atomic => [atomic.Pointer, atomic.Expected, atomic.Operand],
		UnaryOpValue unary => [unary.Operand],
		BinOpValue binary => [binary.Left, binary.Right],
		AssignValue assign => [assign.Left, assign.Right],
		PointerOffsetValue offset => [offset.Pointer, offset.Offset],
		PointerDifferenceValue difference => [difference.Left, difference.Right],
		_ => []
	};
	
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
			
			var unnecessary = new SourceLocation(source, new(range.Start, end));
			diagnostics.Add(DiagnosticReporter.ReportUnreachableCode(new(source, new(range.Start, range.Start))) with
			{
				UnnecessaryLocation = unnecessary,
				Fixes = [DiagnosticFix.Remove(DiagnosticReporter.RemoveUnreachableTitle, unnecessary)]
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