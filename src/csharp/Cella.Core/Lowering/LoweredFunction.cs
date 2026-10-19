using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed record StatementStart
(
	SourceLocation Location,
	BasicBlock Block,
	StatementStart? Parent,
	StatementStart? Previous
);

public sealed class LoweredFunction(FunctionInfo info)
{
	public FunctionInfo Info { get; } = info;
	public ControlFlowGraph Blocks { get; } = new();
	public List<StatementStart> Statements { get; } = [];
	public SourceLocation ExitLocation { get; set; } = SourceLocation.None;
	public bool IsCheckOnly { get; init; }
	public List<SourceLocation> InlinedBodies { get; } = [];
	public List<SourceLocation> InlineReturns { get; } = [];
	
	public bool IsInlined(SourceLocation location) =>
		InlinedBodies.Any(body => Contains(body, location)) && !InlineReturns.Any(value => Contains(value, location));
	
	private static bool Contains(SourceLocation outer, SourceLocation inner) =>
		outer.Source == inner.Source && outer.Range.Contains(inner.Range);
	
	public void Normalize()
	{
		switch (Blocks.Count)
		{
			case 0:
				Blocks.Add(new("entry", Blocks)).SetTerminator(ReturnTerminator.Void);
				break;
			
			default:
			{
				Blocks.RemoveUnreachable();
				IBlockTerminator exit = Info.Signature.ReturnType == NativeSymbols.Void
					? ReturnTerminator.FromValue(null, ExitLocation)
					: new UnreachableTerminator(SourceLocation.None);
				
				foreach (var block in Blocks)
					block.FillTerminator(exit);
				
				break;
			}
		}
	}
}