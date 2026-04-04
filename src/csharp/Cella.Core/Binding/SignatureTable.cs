using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public readonly record struct SignatureTable
{
	public readonly record struct Builder()
	{
		public Dictionary<FileSymbol, ImportEnvironment> ImportEnvironments { get; } = [];
		public Dictionary<FunctionSymbol, FunctionInfo> Functions { get; } = [];
		public Dictionary<ParameterSymbol, TypeSymbol> ParameterTypes { get; } = [];
		
		public SignatureTable Build() => new()
		{
			ImportEnvironments = ImportEnvironments.ToImmutableDictionary(),
			Functions = Functions.ToImmutableDictionary(),
			ParameterTypes = ParameterTypes.ToImmutableDictionary(),
		};
	}
	
	public ImmutableDictionary<FileSymbol, ImportEnvironment> ImportEnvironments { get; init; }
	public ImmutableDictionary<FunctionSymbol, FunctionInfo> Functions { get; init; }
	public ImmutableDictionary<ParameterSymbol, TypeSymbol> ParameterTypes { get; init; }
	
	public static readonly SignatureTable Empty = new()
	{
		ImportEnvironments = [],
		Functions = [],
		ParameterTypes = []
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
			
			foreach (var (param, type) in table.ParameterTypes)
				builder.ParameterTypes[param] = type;
		}
		
		return builder.Build();
	}
}