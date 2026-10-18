using Cella.Compiler.Projects;

namespace Cella.Launcher;

internal static class Installer
{
	public static async Task<int> InstallAsync(string? requested)
	{
		var index = await ReleaseIndex.LoadAsync();
		var version = requested is null
			? index.Stable ?? throw new LauncherException("No release has been published yet")
			: ParseVersion(requested);
		
		if (!index.Releases.TryGetValue(version, out var release))
			throw new LauncherException($"Cella {version} doesn't exist");
		
		var target = Home.GetVersionDirectory(version);
		if (Directory.Exists(target))
		{
			Console.WriteLine($"Cella {version} is already installed");
			return 0;
		}
		
		if (!release.Hosts.TryGetValue(Home.HostName, out var host))
			throw new LauncherException($"Cella {version} isn't available for {Home.HostName}");
		
		foreach (var (name, component) in release.Components)
		{
			if (name.StartsWith("llvm-") && name != $"llvm-{Home.HostName}")
				continue;
			
			var directory = Home.GetComponentDirectory(name, component.Version);
			if (!Directory.Exists(directory))
				await Downloads.InstallAsync(component.File, directory, $"{name} {component.Version}");
		}
		
		await Downloads.InstallAsync(host, target, $"Cella {version}");
		Console.WriteLine($"Installed Cella {version}");
		return 0;
	}
	
	public static int Uninstall(string requested)
	{
		var version = ParseVersion(requested);
		var directory = Home.GetVersionDirectory(version);
		if (!Directory.Exists(directory))
			throw new LauncherException($"Cella {version} isn't installed");
		
		Directory.Delete(directory, true);
		RemoveUnusedComponents();
		Console.WriteLine($"Uninstalled Cella {version}");
		return 0;
	}
	
	public static async Task<int> ListAsync()
	{
		var installed = Home.FindInstalledVersions();
		Console.WriteLine(installed.Count == 0 ? "No versions installed" : "Installed:");
		foreach (var version in installed)
			Console.WriteLine($"  {version}");
		
		ReleaseIndex index;
		try
		{
			index = await ReleaseIndex.LoadAsync();
		}
		catch (LauncherException exception)
		{
			Console.WriteLine(exception.Message);
			return 1;
		}
		
		var available = index.Releases.Keys.Where(version => !installed.Contains(version)).OrderDescending().ToList();
		if (available.Count == 0)
			return 0;
		
		Console.WriteLine("Available:");
		foreach (var version in available)
			Console.WriteLine(version == index.Stable ? $"  {version} (latest)" : $"  {version}");
		
		return 0;
	}
	
	private static void RemoveUnusedComponents()
	{
		var used = Home.FindInstalledVersions()
			.SelectMany(static version => Home.ReadComponents(version))
			.Select(static component => Path.GetFullPath(Home.GetComponentDirectory(component.Key, component.Value)))
			.ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
		
		var llvm = Path.Combine(Home.Directory, "llvm");
		var toolchains = Path.Combine(Home.Directory, "toolchains");
		var folders = (Directory.Exists(llvm) ? Directory.EnumerateDirectories(llvm) : [])
			.Concat(Directory.Exists(toolchains)
				? Directory.EnumerateDirectories(toolchains).SelectMany(Directory.EnumerateDirectories)
				: []);
		
		foreach (var folder in folders.ToList().Where(folder => !used.Contains(Path.GetFullPath(folder))))
			Directory.Delete(folder, true);
		
		var targets = Directory.Exists(toolchains) ? Directory.EnumerateDirectories(toolchains).ToList() : [];
		foreach (var empty in targets.Append(toolchains).Append(llvm).Where(IsEmptyDirectory))
			Directory.Delete(empty);
	}
	
	private static bool IsEmptyDirectory(string folder) =>
		Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any();
	
	public static SemanticVersion ParseVersion(string text) =>
		SemanticVersion.Parse(text) ?? throw new LauncherException($"'{text}' is not a valid version");
}