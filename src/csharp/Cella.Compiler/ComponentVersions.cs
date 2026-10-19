using System.Text.Json;

namespace Cella.Compiler;

public sealed record ComponentVersions(string Llvm, IReadOnlyDictionary<string, string> Toolchains)
{
	public const string FileName = "components.json";
	
	public static ComponentVersions? Load(string versionDirectory)
	{
		var path = Path.Combine(versionDirectory, FileName);
		if (!File.Exists(path))
			return null;
		
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var root = document.RootElement;
		return new ComponentVersions(root.GetProperty("llvm").GetString()!, root.GetProperty("toolchains")
			.EnumerateObject()
			.ToDictionary(static toolchain => toolchain.Name, static toolchain => toolchain.Value.GetString()!));
	}
	
	public static string GetLlvmDirectory(string home, string version) => Path.Combine(home, "llvm", version);
	
	public static string GetToolchainDirectory(string home, string target, string version) =>
		Path.Combine(home, "toolchains", target, version);
	
	public IEnumerable<string> GetDirectories(string home) => Toolchains
		.Select(toolchain => GetToolchainDirectory(home, toolchain.Key, toolchain.Value))
		.Prepend(GetLlvmDirectory(home, Llvm));
}