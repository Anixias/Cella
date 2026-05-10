using Cella.Core.Binding;

namespace Cella.Core.Lowering;

public sealed class LoweredFunction(FunctionInfo info)
{
	public FunctionInfo Info { get; } = info;
	public ControlFlowGraph Blocks { get; } = new();
	
	public void Normalize()
	{
		switch (Blocks.Count)
		{
			case 0:
				Blocks.Add(new("entry", Blocks)).SetTerminator(ReturnTerminator.Void);
				break;
			
			default:
			{
				foreach (var block in Blocks)
					block.FillTerminator(ReturnTerminator.Void);
				
				break;
			}
		}
	}
}