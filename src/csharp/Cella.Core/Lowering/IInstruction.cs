using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public interface IInstruction
{
	SourceLocation SourceLocation { get; }
}

public sealed class BeginScopeInstruction(int scopeId, SourceLocation sourceLocation) : IInstruction
{
	public int ScopeId { get; } = scopeId;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}

public sealed class EndScopeInstruction(int scopeId, SourceLocation sourceLocation) : IInstruction
{
	public int ScopeId { get; } = scopeId;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}

// TODO: Local vars that are never mutated should be inlined
public sealed class LocalVarInstruction(LocalVariableSymbol symbol, Value initializer,
	SourceLocation sourceLocation, int scopeId = 0) : IInstruction
{
	public LocalVariableSymbol Symbol { get; } = symbol;
	public Value Initializer { get; } = initializer;
	public int ScopeId { get; } = scopeId;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}

public sealed class ExpressionInstruction(Value value) : IInstruction
{
	public Value Value { get; } = value;
	public SourceLocation SourceLocation { get; } = value.SourceLocation;
}

public sealed class DropInstruction(Value value, SourceLocation sourceLocation) : IInstruction
{
	public Value Value { get; } = value;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}
