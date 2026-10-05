using System.Collections.Immutable;
using Cella.Core.Binding;

namespace Cella.Core.Symbols;

// TODO Type parameters
public sealed class FunctionSignature
(
	IEnumerable<TypeSymbol> parameterTypes,
	TypeSymbol returnType,
	bool isVariadic = false,
	IEnumerable<ParameterMode>? parameterModes = null
) : ICallable
{
	public ImmutableArray<TypeSymbol> ParameterTypes { get; } = parameterTypes.ToImmutableArray();
	public TypeSymbol ReturnType { get; } = returnType;
	public bool IsVariadic { get; } = isVariadic;
	public ImmutableArray<ParameterMode> ParameterModes { get; } = parameterModes?.ToImmutableArray() ?? [];
	public bool HasMutParameter => ParameterModes.Contains(ParameterMode.Mut);
	
	public ParameterMode GetMode(int index) =>
		index < ParameterModes.Length ? ParameterModes[index] : ParameterMode.ReadOnly;
	
	public TypeSymbol GetDeclaredType(int index) =>
		GetMode(index) == ParameterMode.Mut && ParameterTypes[index] is PointerType pointer
			? pointer.BaseType
			: ParameterTypes[index];
}