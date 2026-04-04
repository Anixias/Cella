using System.Collections.Immutable;

namespace Cella.Core.Symbols;

// TODO Type parameters
public sealed class FunctionSignature(IEnumerable<TypeSymbol> parameterTypes, TypeSymbol returnType)
{
	public ImmutableArray<TypeSymbol> ParameterTypes { get; } = parameterTypes.ToImmutableArray();
	public TypeSymbol ReturnType { get; } = returnType;
}