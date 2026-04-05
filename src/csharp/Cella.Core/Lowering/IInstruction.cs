using Cella.Core.Symbols;

namespace Cella.Core.Lowering;

public interface IInstruction;

// TODO: Local vars that are never mutated should be inlined
public sealed class LocalVarInstruction(LocalVariableSymbol symbol, Value? initializer) : IInstruction
{
	public LocalVariableSymbol Symbol { get; } = symbol;
	public Value? Initializer { get; } = initializer;
}

public sealed class ExpressionInstruction(Value value) : IInstruction
{
	public Value Value { get; } = value;
}