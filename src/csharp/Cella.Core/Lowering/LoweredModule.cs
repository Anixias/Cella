using Cella.Core.Symbols;

namespace Cella.Core.Lowering;

// TODO Add types
public sealed class LoweredModule(ModuleSymbol symbol)
{
	public ModuleSymbol Symbol { get; } = symbol;
	public List<LoweredFunction> Functions { get; } = [];
}