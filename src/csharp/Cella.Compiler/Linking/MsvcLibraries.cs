using System.Diagnostics;
using Microsoft.Win32;

namespace Cella.Compiler.Linking;

public sealed record MsvcLibraries(string Msvc, string Ucrt, string Um)
{
	private static readonly Lazy<MsvcLibraries?> _found = new(Find);
	
	public static MsvcLibraries? Current => _found.Value;
	
	private static MsvcLibraries? Find()
	{
		if (!OperatingSystem.IsWindows())
			return null;
		
		if (FindVisualStudio() is not { } tools || FindWindowsSdk() is not var (ucrt, um))
			return null;
		
		return new MsvcLibraries(tools, ucrt, um);
	}
	
	private static string? FindVisualStudio()
	{
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
		if (!File.Exists(vswhere))
			return null;
		
		string installation;
		try
		{
			using var process = Process.Start(new ProcessStartInfo(vswhere)
			{
				ArgumentList =
				{
					"-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
					"-property", "installationPath"
				},
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			
			if (process is null)
				return null;
			
			installation = process.StandardOutput.ReadToEnd().Trim();
			process.WaitForExit();
		}
		catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
		{
			return null;
		}
		
		var versionFile =
			Path.Combine(installation, "VC", "Auxiliary", "Build", "Microsoft.VCToolsVersion.default.txt");
		
		if (installation.Length == 0 || !File.Exists(versionFile))
			return null;
		
		var libraries = Path.Combine(installation, "VC", "Tools", "MSVC", File.ReadAllText(versionFile).Trim(), "lib",
			"x64");
		
		return File.Exists(Path.Combine(libraries, "msvcrt.lib")) ? libraries : null;
	}
	
	private static (string Ucrt, string Um)? FindWindowsSdk()
	{
		if (!OperatingSystem.IsWindows())
			return null;
		
		using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
			.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
		
		var root = key?.GetValue("KitsRoot10") as string ??
		           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits",
			           "10");
		
		var libraries = Path.Combine(root, "Lib");
		if (!Directory.Exists(libraries))
			return null;
		
		return Directory.EnumerateDirectories(libraries)
			.Select(static directory => (Version: Version.TryParse(Path.GetFileName(directory), out var version)
				? version
				: null, Directory: directory))
			.Where(static entry => entry.Version is not null)
			.OrderByDescending(static entry => entry.Version)
			.Select(static entry => (Ucrt: Path.Combine(entry.Directory, "ucrt", "x64"),
				Um: Path.Combine(entry.Directory, "um", "x64")))
			.Where(static entry => File.Exists(Path.Combine(entry.Ucrt, "ucrt.lib")) &&
			                       File.Exists(Path.Combine(entry.Um, "kernel32.lib")))
			.Select(static entry => ((string, string)?)entry)
			.FirstOrDefault();
	}
}