using Cella.Core.Symbols;

namespace Cella.Core.Lowering;

public interface IInstruction;

public sealed class LocalVarInstruction(LocalVariableSymbol symbol, Value? initializer) : IInstruction
{
	public LocalVariableSymbol Symbol { get; } = symbol;
	public Value? Initializer { get; } = initializer;
}