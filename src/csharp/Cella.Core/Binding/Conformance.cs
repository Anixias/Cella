using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class Conformance
(
	TraitSymbol trait,
	ImmutableArray<TypeSymbol> arguments,
	TypeSymbol target,
	ImmutableArray<TypeParameterSymbol> parameters,
	ImplSymbol? impl,
	SourceLocation location
)
{
	private readonly Dictionary<FunctionSymbol, Witness> _witnesses = [];
	private readonly Dictionary<GlobalSymbol, GlobalSymbol> _constantWitnesses = [];
	
	public TraitSymbol Trait { get; } = trait;
	public ImmutableArray<TypeSymbol> Arguments { get; } = arguments;
	public TypeSymbol Target { get; } = target;
	public ImmutableArray<TypeParameterSymbol> Parameters { get; } = parameters;
	public ImplSymbol? Impl { get; } = impl;
	public SourceLocation Location { get; } = location;
	public bool IsImplied { get; init; }
	public bool IsConstant { get; init; }
	public bool IsForwarded { get; init; }
	public Conformance? Template { get; init; }
	public ImmutableArray<TypeSymbol> FreeArguments { get; init; } = [];
	public bool IsTemplate => Template is null && Impl is { FreeParameters.IsEmpty: false };
	public Dictionary<FunctionSymbol, Witness> Witnesses => Template?._witnesses ?? _witnesses;
	
	public Dictionary<GlobalSymbol, GlobalSymbol> ConstantWitnesses =>
		Template?._constantWitnesses ?? _constantWitnesses;
	
	public ErrorContainment.Owner Owner { get; set; }
}

public readonly record struct BorrowForwarding(bool ThroughImm, bool ThroughMut, bool CopiesSelf);
public abstract record Witness;
public sealed record FunctionWitness(FunctionSymbol Function, FunctionInfo Info) : Witness;
public sealed record NativeWitness(OperationImpl Operation, bool IsCompound = false) : Witness;
public sealed record MemberwiseWitness : Witness;
public sealed record DefaultWitness : Witness;
public sealed record ConversionWitness(Conversion Conversion) : Witness;
public sealed record SelfConversionWitness(Conversion Conversion) : Witness;