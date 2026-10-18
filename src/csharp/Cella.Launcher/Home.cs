using System.Runtime.InteropServices;
using System.Text.Json;
using Cella.Compiler.Projects;

namespace Cella.Launcher;

internal static class Home
{
	public const string ComponentsFileName = "components.json";
	
	public static string Directory => Environment.GetEnvironmentVariable("CELLA_HOME") is { Length: > 0 } home
		? Path.GetFullPath(home)
		: OperatingSystem.IsWindows()
			? @"C:\cella"
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cella");
	
	public static string HostName => (OperatingSystem.IsWindows() ? "win" : "linux") + "-" +
	                                 RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
	
	public static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
	
	public static string GetVersionDirectory(SemanticVersion version) => Path.Combine(Directory, version.ToString());
	
	public static string GetComponentDirectory(string name, string version) => name.StartsWith("llvm-")
		? Path.Combine(Directory, "llvm", version)
		: Path.Combine(Directory, "toolchains", name, version);
	
	public static List<SemanticVersion> FindInstalledVersions()
	{
		if (!System.IO.Directory.Exists(Directory))
			return [];
		
		return System.IO.Directory.EnumerateDirectories(Directory)
			.Select(static directory => SemanticVersion.Parse(Path.GetFileName(directory)))
			.OfType<SemanticVersion>()
			.Where(static version => File.Exists(Path.Combine(GetVersionDirectory(version), ComponentsFileName)))
			.OrderDescending()
			.ToList();
	}
	
	public static Dictionary<string, string> ReadComponents(SemanticVersion version)
	{
		var path = Path.Combine(GetVersionDirectory(version), ComponentsFileName);
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		return document.RootElement.EnumerateObject()
			.Where(static property => property.Value.ValueKind == JsonValueKind.String)
			.ToDictionary(static property => property.Name, static property => property.Value.GetString()!);
	}
}