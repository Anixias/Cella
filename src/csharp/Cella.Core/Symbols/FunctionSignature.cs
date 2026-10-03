using System.Collections.Immutable;
using Cella.Core.Binding;

namespace Cella.Core.Symbols;

// TODO Type parameters
public sealed class FunctionSignature
(
	IEnumerable<TypeSymbol> parameterTypes,
	TypeSymbol returnType,
	bool isVariadic = false
) : ICallable
{
	public ImmutableArray<TypeSymbol> ParameterTypes { get; } = parameterTypes.ToImmutableArray();
	public TypeSymbol ReturnType { get; } = returnType;
	public bool IsVariadic { get; } = isVariadic;
}