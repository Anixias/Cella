using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public readonly record struct FunctionInfo
(
	string? MangledName,
	FunctionSymbol Symbol,
	FunctionSignature Signature,
	Scope? Scope,
	string? Origin,
	FileSymbol File
);

public readonly record struct VariableInfo(VariableSymbol Symbol, TypeSymbol Type);