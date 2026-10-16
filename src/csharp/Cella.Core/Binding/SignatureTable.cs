using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public readonly record struct SignatureTable
{
	public readonly record struct Builder()
	{
		public Dictionary<FileSymbol, ImportEnvironment> ImportEnvironments { get; } = [];
		public Dictionary<FunctionSymbol, FunctionInfo> Functions { get; } = [];
		public Dictionary<VariableSymbol, TypeSymbol> VariableTypes { get; } = [];
		public Dictionary<GlobalSymbol, GlobalInfo> Globals { get; } = [];
		public Dictionary<TypeSymbol, ISize> TypeSizes { get; } = [];
		public Dictionary<FunctionSymbol, InlineDeclaration> InlineContexts { get; } = [];
		public Dictionary<string, FunctionSymbol> PanicTargets { get; } = [];
		
		public SignatureTable Build() => new()
		{
			ImportEnvironments = ImportEnvironments.ToImmutableDictionary(),
			Functions = Functions.ToImmutableDictionary(),
			VariableTypes = VariableTypes.ToImmutableDictionary(),
			Globals = Globals.ToImmutableDictionary(),
			TypeSizes = TypeSizes.ToImmutableDictionary(),
			InlineContexts = InlineContexts.ToImmutableDictionary(),
			PanicTargets = PanicTargets
		};
	}
	
	public ImmutableDictionary<FileSymbol, ImportEnvironment> ImportEnvironments { get; init; }
	public ImmutableDictionary<FunctionSymbol, FunctionInfo> Functions { get; init; }
	public ImmutableDictionary<VariableSymbol, TypeSymbol> VariableTypes { get; init; }
	public ImmutableDictionary<GlobalSymbol, GlobalInfo> Globals { get; init; }
	public ImmutableDictionary<TypeSymbol, ISize> TypeSizes { get; init; }
	public ImmutableDictionary<FunctionSymbol, InlineDeclaration> InlineContexts { get; init; }
	public IReadOnlyDictionary<string, FunctionSymbol> PanicTargets { get; init; }
	
	public static readonly SignatureTable Empty = new()
	{
		ImportEnvironments = [],
		Functions = [],
		VariableTypes = [],
		Globals = [],
		TypeSizes = [],
		InlineContexts = [],
		PanicTargets = new Dictionary<string, FunctionSymbol>()
	};
	
	public static SignatureTable Combine(params IEnumerable<SignatureTable> tables)
	{
		var builder = new Builder();
		
		foreach (var table in tables)
		{
			foreach (var (file, importEnvironment) in table.ImportEnvironments)
				builder.ImportEnvironments[file] = importEnvironment;
			
			foreach (var (function, info) in table.Functions)
				builder.Functions[function] = info;
			
			foreach (var (var, type) in table.VariableTypes)
				builder.VariableTypes[var] = type;
			
			foreach (var (global, info) in table.Globals)
				builder.Globals[global] = info;
			
			foreach (var (type, size) in table.TypeSizes)
				builder.TypeSizes[type] = size;
			
			foreach (var (function, declaration) in table.InlineContexts)
				builder.InlineContexts[function] = declaration;
			
			foreach (var (path, target) in table.PanicTargets)
				builder.PanicTargets[path] = target;
		}
		
		return builder.Build();
	}
}