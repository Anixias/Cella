using Cella.Core.Binding;
using Cella.Core.Symbols;

namespace Cella.Core.Lowering;

public sealed class LoweredModule(ModuleSymbol symbol)
{
	public ModuleSymbol Symbol { get; } = symbol;
	public List<LoweredFile> Files { get; } = [];
}

public sealed class LoweredFile(FileSymbol symbol)
{
	public FileSymbol Symbol { get; } = symbol;
	public List<TypeSymbol> Types { get; } = [];
	public List<LoweredFunction> Functions { get; } = [];
	public List<FunctionInfo> ImportedFunctions { get; } = [];
	public List<FunctionInfo> ExternalFunctions { get; } = [];
}