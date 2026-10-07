using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class Conformance
(
	TraitSymbol trait,
	TypeSymbol target,
	ImmutableArray<TypeParameterSymbol> parameters,
	ImplSymbol? impl,
	SourceLocation location
)
{
	public TraitSymbol Trait { get; } = trait;
	public TypeSymbol Target { get; } = target;
	public ImmutableArray<TypeParameterSymbol> Parameters { get; } = parameters;
	public ImplSymbol? Impl { get; } = impl;
	public SourceLocation Location { get; } = location;
	public Dictionary<FunctionSymbol, Witness> Witnesses { get; } = [];
}

public abstract record Witness;
public sealed record FunctionWitness(FunctionSymbol Function, FunctionInfo Info) : Witness;
public sealed record NativeWitness(NativeImpl Operation, bool IsCompound = false) : Witness;
public sealed record MemberwiseWitness : Witness;
public sealed record DefaultWitness : Witness;
public sealed record ConversionWitness(Conversion Conversion) : Witness;