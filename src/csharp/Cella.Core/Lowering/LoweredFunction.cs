using Cella.Core.Symbols;

namespace Cella.Core.Lowering;

public sealed class LoweredFunction(FunctionSymbol symbol)
{
	public FunctionSymbol Symbol { get; } = symbol;
	public List<BasicBlock> Blocks { get; } = [];
}