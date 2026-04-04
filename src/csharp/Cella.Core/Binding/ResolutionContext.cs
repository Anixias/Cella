using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public readonly struct ResolutionContext
{
	public FileSymbol File { get; init; }
	public TypeSymbol? ContainingType { get; init; }
	public FunctionSymbol? ContainingFunction { get; init; }
	public ImportEnvironment? Imports { get; init; }
	public Scope? LocalScope { get; init; }
	
	private static Symbol? ResolveFrom(string name, ImmutableArray<Symbol> candidates) => candidates switch
	{
		{ Length: > 1 } => new AmbiguousSymbol(name, candidates),
		{ Length: 1 } => candidates[0],
		_ => null
	};
	
	public Symbol? Resolve(string name)
	{
		// TODO None of this will work with function overloads :(
		
		if (LocalScope?.Resolve(name) is { } localSymbol)
			return localSymbol;
		
		// TODO Also check type parameters
		if (ContainingFunction?.Parameters.GetValueOrDefault(name) is { } param)
			return param;
		
		for (var type = ContainingType; type is not null; type = type.ContainingType)
			if (type.Children.TryGetValue(name, out var member))
				return member;
		
		if (File.Symbols.TryGetValue(name, out var fileSymbol))
			return fileSymbol;
		
		if (Imports?.Resolve(name) is { Length: > 0 } imports)
			return ResolveFrom(name, imports);
		
		return NativeSymbols.Resolve(name);
	}
}