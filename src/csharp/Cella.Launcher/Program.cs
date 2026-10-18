using System.Diagnostics;
using Cella.Compiler.Projects;

namespace Cella.Launcher;

internal sealed class LauncherException(string message) : Exception(message);

internal static class Program
{
	private const string Usage =
		"Usage: cella <command> [arguments]\n" +
		"\n" +
		"  build [project file or folder]  Build a project with its pinned Cella version\n" +
		"  install [version]               Install a version (the latest when none is given)\n" +
		"  uninstall <version>             Remove a version\n" +
		"  list                            List installed and available versions\n" +
		"  lsp                             Start the language server for the current folder\n" +
		"  self update                     Update this launcher\n" +
		"  self uninstall                  Remove Cella completely\n" +
		"  +<version> <command>            Run one command with another installed version";
	
	public static async Task<int> Main(string[] args)
	{
		SelfCommands.RemoveReplacedLauncher();
		try
		{
			SemanticVersion? chosen = null;
			if (args is [var first, ..] && first.StartsWith('+'))
			{
				chosen = Installer.ParseVersion(first[1..]);
				args = args[1..];
			}
			
			return args switch
			{
				[] or ["help"] => PrintUsage(0),
				["install"] => await Installer.InstallAsync(null),
				["install", var version] => await Installer.InstallAsync(version),
				["uninstall", var version] => Installer.Uninstall(version),
				["list"] => await Installer.ListAsync(),
				["self", "install"] => SelfCommands.Install(),
				["self", "uninstall"] => SelfCommands.Uninstall(false),
				["self", "uninstall", "--yes"] => SelfCommands.Uninstall(true),
				["self", "update"] => await SelfCommands.UpdateAsync(),
				["install" or "uninstall" or "list" or "self", ..] => PrintUsage(1),
				["lsp", .. var rest] => Run(chosen ?? Pins.FindHighest(Environment.CurrentDirectory), "cella-lsp",
					rest),
				[_, .. var rest] => Run(
					chosen ?? Pins.FindHighest(Pins.FindProjectPath(rest) ?? Environment.CurrentDirectory),
					"cella", args)
			};
		}
		catch (LauncherException exception)
		{
			await Console.Error.WriteLineAsync(exception.Message);
			return 1;
		}
	}
	
	private static int PrintUsage(int exitCode)
	{
		Console.WriteLine(Usage);
		return exitCode;
	}
	
	private static int Run(SemanticVersion? pinned, string program, string[] args)
	{
		var installed = Home.FindInstalledVersions();
		if (installed.Count == 0)
			throw new LauncherException("No Cella version is installed. Run 'cella install'.");
		
		var version = pinned ?? installed[0];
		if (!installed.Contains(version))
			throw new LauncherException($"Cella {version} isn't installed. Run 'cella install {version}'.");
		
		var path = Path.Combine(Home.GetVersionDirectory(version), Home.Executable(program));
		var start = new ProcessStartInfo(path) { UseShellExecute = false };
		foreach (var argument in args)
			start.ArgumentList.Add(argument);
		
		using var process = Process.Start(start) ?? throw new LauncherException($"Couldn't start '{path}'");
		process.WaitForExit();
		return process.ExitCode;
	}
}