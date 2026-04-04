using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public readonly record struct FunctionInfo
(
	string? MangledName,
	FunctionSymbol Symbol,
	FunctionSignature Signature,
	Scope Scope
);

public readonly record struct VariableInfo(VariableSymbol Symbol, TypeSymbol Type);