using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Analysis;

public enum AccessKind
{
	Read,
	Borrow,
	Move
}

public abstract record MemoryEvent(SourceLocation Location);
public sealed record AccessEvent(Place Place, AccessKind Kind, SourceLocation Location) : MemoryEvent(Location);
public sealed record WriteEvent(Place Place, SourceLocation Location) : MemoryEvent(Location);

public sealed record DefineEvent(LocalVariableSymbol Local, bool IsUndef, SourceLocation Location)
	: MemoryEvent(Location);

public sealed record DropEvent(Place Place, DropInstruction Instruction) : MemoryEvent(Instruction.SourceLocation);
public sealed record StorageDeadEvent(LocalVariableSymbol Local, SourceLocation Location) : MemoryEvent(Location);