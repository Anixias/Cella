using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public readonly record struct FunctionInfo(FunctionSymbol Symbol, FunctionSignature Signature, Scope Scope);
public readonly record struct VariableInfo(VariableSymbol Symbol, TypeSymbol Type);