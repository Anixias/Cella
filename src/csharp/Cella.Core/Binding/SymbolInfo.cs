using System.Collections.Immutable;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public readonly record struct FunctionInfo
(
	string? MangledName,
	FunctionSymbol Symbol,
	FunctionSignature Signature,
	Scope? Scope,
	string? Origin,
	FileSymbol? File
)
{
	public ImmutableArray<TypeSymbol> TypeArguments { get; init; } = [];
	public string? MangledBase { get; init; }
	public FunctionSignature? Declared { get; init; }
	public FunctionSignature DeclaredSignature => Declared ?? Signature;
	
	public bool Equals(FunctionInfo other) =>
		MangledName == other.MangledName && Symbol == other.Symbol && Signature == other.Signature &&
		Scope == other.Scope && Origin == other.Origin && File == other.File && Declared == other.Declared &&
		TypeArguments.AsSpan().SequenceEqual(other.TypeArguments.AsSpan());
	
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(MangledName);
		hash.Add(Symbol);
		hash.Add(Signature);
		hash.Add(Scope);
		hash.Add(Origin);
		hash.Add(File);
		hash.Add(Declared);
		foreach (var argument in TypeArguments.AsSpan())
			hash.Add(argument);
		
		return hash.ToHashCode();
	}
}

public readonly record struct NamePart(string Name, ImmutableArray<TypeSymbol> Arguments);
public readonly record struct VariableInfo(VariableSymbol Symbol, TypeSymbol Type);
public readonly record struct StaticFault(string Message, SourceLocation Location);

public readonly record struct GlobalInfo
(
	string MangledName,
	GlobalSymbol Symbol,
	TypeSymbol Type,
	IResolvedExpressionNode? Initializer,
	Constant? Value,
	FileSymbol File
)
{
	public StaticFault? Fault { get; init; }
}