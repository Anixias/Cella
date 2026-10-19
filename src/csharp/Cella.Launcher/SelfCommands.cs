using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cella.Compiler.Projects;
using Microsoft.Win32;

namespace Cella.Launcher;

internal static partial class SelfCommands
{
	private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Cella";
	
	private static string LauncherPath => Path.Combine(Home.Directory, Home.Executable("cella"));
	
	private static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".local", "bin", "cella");
	
	public static void RemoveReplacedLauncher()
	{
		try
		{
			File.Delete(LauncherPath + ".old");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}
	
	public static int Install()
	{
		if (!File.Exists(LauncherPath))
			throw new LauncherException($"The launcher must be at '{LauncherPath}'");
		
		if (OperatingSystem.IsWindows())
		{
			AddToPath(Home.Directory);
			RegisterUninstaller();
			Console.WriteLine($"Added '{Home.Directory}' to your PATH");
			return 0;
		}
		
		Directory.CreateDirectory(Path.GetDirectoryName(LinkPath)!);
		if (File.Exists(LinkPath) || new FileInfo(LinkPath).LinkTarget is not null)
			File.Delete(LinkPath);
		
		File.CreateSymbolicLink(LinkPath, LauncherPath);
		Console.WriteLine($"Linked '{LinkPath}'");
		var directory = Path.GetDirectoryName(LinkPath)!;
		var path = Environment.GetEnvironmentVariable("PATH") ?? "";
		if (!path.Split(':').Contains(directory))
			Console.WriteLine($"Add it to your PATH with: export PATH=\"$HOME/.local/bin:$PATH\"");
		
		return 0;
	}
	
	public static int Uninstall(bool confirmed)
	{
		if (!confirmed)
		{
			Console.Write($"Remove Cella and every installed version from '{Home.Directory}'? [y/N] ");
			if (Console.ReadLine()?.Trim() is not ("y" or "Y" or "yes"))
				return 1;
		}
		
		if (OperatingSystem.IsWindows())
		{
			RemoveFromPath(Home.Directory);
			Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
			var parked = Path.Combine(Path.GetTempPath(), $"cella-{Guid.NewGuid():N}.exe");
			File.Move(LauncherPath, parked);
			Process.Start(new ProcessStartInfo("cmd.exe")
			{
				Arguments = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{parked}\"",
				WorkingDirectory = Path.GetTempPath(),
				UseShellExecute = false,
				CreateNoWindow = true
			});
			
			try
			{
				Directory.Delete(Home.Directory, true);
			}
			catch (IOException)
			{
				if (Directory.EnumerateFileSystemEntries(Home.Directory).Any())
					throw;
				
				Console.WriteLine($"Cella has been uninstalled, but '{Home.Directory}' is in use and wasn't removed");
				return 0;
			}
		}
		else
		{
			if (new FileInfo(LinkPath).LinkTarget is { } target &&
			    Path.GetFullPath(target).Equals(Path.GetFullPath(LauncherPath), StringComparison.Ordinal))
				File.Delete(LinkPath);
			
			Directory.Delete(Home.Directory, true);
		}
		
		Console.WriteLine("Cella has been uninstalled");
		return 0;
	}
	
	public static async Task<int> UpdateAsync()
	{
		var index = await ReleaseIndex.LoadAsync();
		if (index.Launcher is not { } launcher || !launcher.Hosts.TryGetValue(Home.HostName, out var file))
			throw new LauncherException($"No launcher is published for {Home.HostName}");
		
		if (launcher.Version.CompareTo(SemanticVersion.Current) <= 0)
		{
			Console.WriteLine($"The launcher is up to date ({SemanticVersion.Current})");
			return 0;
		}
		
		var replacement = LauncherPath + ".new";
		await Downloads.DownloadAsync(file, replacement);
		if (OperatingSystem.IsWindows())
		{
			File.Move(LauncherPath, LauncherPath + ".old", true);
			File.Move(replacement, LauncherPath);
		}
		else
		{
			File.SetUnixFileMode(replacement, File.GetUnixFileMode(LauncherPath));
			File.Move(replacement, LauncherPath, true);
		}
		
		Console.WriteLine($"Updated the launcher to {launcher.Version}");
		return 0;
	}
	
	[SupportedOSPlatform("windows")]
	private static void AddToPath(string directory)
	{
		using var key = Registry.CurrentUser.CreateSubKey("Environment");
		var entries = ReadPath(key);
		if (entries.Any(entry => IsSameDirectory(entry, directory)))
			return;
		
		WritePath(key, [directory, ..entries]);
	}
	
	[SupportedOSPlatform("windows")]
	private static void RemoveFromPath(string directory)
	{
		using var key = Registry.CurrentUser.CreateSubKey("Environment");
		var entries = ReadPath(key);
		var kept = entries.Where(entry => !IsSameDirectory(entry, directory)).ToList();
		if (kept.Count != entries.Count)
			WritePath(key, kept);
	}
	
	[SupportedOSPlatform("windows")]
	private static List<string> ReadPath(RegistryKey key) =>
	[
		..(key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "")
		.Split(';', StringSplitOptions.RemoveEmptyEntries)
	];
	
	[SupportedOSPlatform("windows")]
	private static void WritePath(RegistryKey key, IEnumerable<string> entries)
	{
		key.SetValue("Path", string.Join(';', entries), RegistryValueKind.ExpandString);
		SendMessageTimeout(new nint(0xFFFF), 0x001A, nint.Zero, "Environment", 0x0002, 5000, out _);
	}
	
	[SupportedOSPlatform("windows")]
	private static void RegisterUninstaller()
	{
		using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
		key.SetValue("DisplayName", "Cella");
		key.SetValue("DisplayVersion", SemanticVersion.Current.ToString());
		key.SetValue("InstallLocation", Home.Directory);
		key.SetValue("UninstallString", $"\"{LauncherPath}\" self uninstall");
		key.SetValue("NoModify", 1, RegistryValueKind.DWord);
		key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
	}
	
	private static bool IsSameDirectory(string first, string second) => string.Equals(
		Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second),
		StringComparison.OrdinalIgnoreCase);
	
	[LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
	[SuppressMessage("ReSharper", "UnusedMethodReturnValue.Local")]
	private static partial nint SendMessageTimeout(nint window, uint message, nint wParam, string lParam,
		uint flags, uint timeout, out nint result);
}