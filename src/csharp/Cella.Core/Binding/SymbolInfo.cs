using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
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

public readonly record struct GlobalInfo
(
	string MangledName,
	GlobalSymbol Symbol,
	TypeSymbol Type,
	IResolvedExpressionNode Initializer,
	Constant? Value,
	FileSymbol File
);