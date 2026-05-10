using Cella.Core.Binding;

namespace Cella.Core.Lowering;

public sealed class LoweredFunction(FunctionInfo info)
{
	public FunctionInfo Info { get; } = info;
	public ControlFlowGraph Blocks { get; } = new();
}