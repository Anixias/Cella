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
		
		public SignatureTable Build() => new()
		{
			ImportEnvironments = ImportEnvironments.ToImmutableDictionary(),
			Functions = Functions.ToImmutableDictionary(),
			VariableTypes = VariableTypes.ToImmutableDictionary(),
		};
	}
	
	public ImmutableDictionary<FileSymbol, ImportEnvironment> ImportEnvironments { get; init; }
	public ImmutableDictionary<FunctionSymbol, FunctionInfo> Functions { get; init; }
	public ImmutableDictionary<VariableSymbol, TypeSymbol> VariableTypes { get; init; }
	
	public static readonly SignatureTable Empty = new()
	{
		ImportEnvironments = [],
		Functions = [],
		VariableTypes = []
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
			
			foreach (var (param, type) in table.VariableTypes)
				builder.VariableTypes[param] = type;
		}
		
		return builder.Build();
	}
}