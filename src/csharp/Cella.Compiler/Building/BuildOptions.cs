using Cella.Core.CodeGen;

namespace Cella.Compiler.Building;

public sealed record BuildOptions
{
	public required TargetTriple Target { get; init; }
	public OptimizeMode OptimizeMode { get; init; } = OptimizeMode.Debug;
	public IReadOnlyDictionary<string, bool> FlagOverrides { get; init; } = new Dictionary<string, bool>();
	public bool Verbose { get; init; }
	public bool EmitsIr { get; init; }
	public bool EmitsAssembly { get; init; }
	public bool ChecksOnly { get; init; }
	public string? Entry { get; init; }
}