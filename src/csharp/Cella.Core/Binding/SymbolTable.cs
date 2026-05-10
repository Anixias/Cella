using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding;

// Contains the symbols for an entire project/assembly
public readonly record struct SymbolTable
{
	public readonly record struct Builder()
	{
		public Dictionary<ModuleName, ModuleSymbol> ModuleSymbols { get; } = [];
		public Dictionary<ModuleSymbol, HashSet<Symbol>> SymbolsByModule { get; } = [];
		public Dictionary<IDeclarationNode, Symbol> DeclarationSymbols { get; } = [];
		
		public SymbolTable Build() => new()
		{
			ModuleSymbols = ModuleSymbols.ToImmutableDictionary(),
			SymbolsByModule = SymbolsByModule.ToImmutableDictionary(static kvp => kvp.Key,
				static kvp => kvp.Value
					.GroupBy(static s => s.Name)
					.ToImmutableDictionary(static g => g.Key, static g => g.ToHashSet())),
			DeclarationSymbols = DeclarationSymbols.ToImmutableDictionary()
		};
	}
	
	public ImmutableDictionary<ModuleName, ModuleSymbol> ModuleSymbols { get; init; }
	
	public ImmutableDictionary<ModuleSymbol, ImmutableDictionary<string, HashSet<Symbol>>> SymbolsByModule
	{
		get;
		init;
	}
	
	public ImmutableDictionary<IDeclarationNode, Symbol> DeclarationSymbols { get; init; }
	
	public static readonly SymbolTable Empty = new()
	{
		ModuleSymbols = [],
		SymbolsByModule = [],
		DeclarationSymbols = []
	};
}