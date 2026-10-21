using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;

namespace Cella.Core.Symbols;

public sealed class FunctionSignature
(
	IEnumerable<TypeSymbol> parameterTypes,
	TypeSymbol returnType,
	bool isVariadic = false,
	IEnumerable<ParameterMode>? parameterModes = null,
	bool hasVariadicParameter = false,
	IEnumerable<Constant>? defaults = null
) : ICallable
{
	public ImmutableArray<TypeSymbol> ParameterTypes { get; } = parameterTypes.ToImmutableArray();
	public TypeSymbol ReturnType { get; } = returnType;
	public bool IsVariadic { get; } = isVariadic;
	public bool HasVariadicParameter { get; } = hasVariadicParameter;
	public ImmutableArray<Constant> Defaults { get; } = defaults?.ToImmutableArray() ?? [];
	public int FixedCount => ParameterTypes.Length - (HasVariadicParameter ? 1 : 0);
	public ImmutableArray<ParameterMode> ParameterModes { get; } = parameterModes?.ToImmutableArray() ?? [];
	
	public ParameterMode GetMode(int index) =>
		index < ParameterModes.Length ? ParameterModes[index] : ParameterMode.Value;
	
	public TypeSymbol GetDeclaredType(int index) => GetMode(index).GetDeclaredType(ParameterTypes[index]);
}