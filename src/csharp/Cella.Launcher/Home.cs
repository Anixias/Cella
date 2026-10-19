using System.Runtime.InteropServices;
using Cella.Compiler;
using Cella.Compiler.Projects;

namespace Cella.Launcher;

internal static class Home
{
	public static string Directory => Environment.GetEnvironmentVariable("CELLA_HOME") is { Length: > 0 } home
		? Path.GetFullPath(home)
		: OperatingSystem.IsWindows()
			? @"C:\cella"
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cella");
	
	public static string HostName => OperatingSystemName + "-" +
	                                 RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
	
	private static string OperatingSystemName => OperatingSystem.IsWindows()
		? "win"
		: OperatingSystem.IsLinux()
			? "linux"
			: throw new LauncherException("Cella doesn't run on this operating system");
	
	public static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
	
	public static string GetVersionDirectory(SemanticVersion version) => Path.Combine(Directory, version.ToString());
	
	public static List<SemanticVersion> FindInstalledVersions()
	{
		if (!System.IO.Directory.Exists(Directory))
			return [];
		
		return System.IO.Directory.EnumerateDirectories(Directory)
			.Select(static directory => SemanticVersion.Parse(Path.GetFileName(directory)))
			.OfType<SemanticVersion>()
			.Where(static version =>
				File.Exists(Path.Combine(GetVersionDirectory(version), ComponentVersions.FileName)))
			.OrderDescending()
			.ToList();
	}
	
	public static ComponentVersions ReadComponents(SemanticVersion version) =>
		ComponentVersions.Load(GetVersionDirectory(version)) ??
		throw new LauncherException($"Cella {version} is missing its {ComponentVersions.FileName}");
}