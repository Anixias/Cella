using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Cella.Compiler.Building;
using Cella.Compiler.Projects;
using Cella.Core.CodeGen;

namespace Cella.Compiler;

internal static class Program
{
	public static Task<int> Main(string[] args)
	{
		Installation.UseComponentLibraries();
		if (Console.IsOutputRedirected)
			Console.OutputEncoding = new UTF8Encoding(false);
		
		return CompilerThread.Run(() => RunAsync(args));
	}
	
	private static readonly string[] _options =
		["--verbose", "--hints", "--release", "--flag", "--target", "--entry", "--emit-ir", "--emit-asm"];
	
	private const string Usage =
		"Usage: cella build [project file or folder] [--release] [--verbose] [--hints] [--flag <name>[=true|false]] " +
		"[--target windows-x64|windows-x64-gnu|linux-x64] [--entry <module.function>] [--emit-ir] [--emit-asm]\n" +
		"       cella run [project file or folder] [build options] [-- program arguments]\n" +
		"       cella new [name] [--lib] [--gitignore]\n" +
		"       cella --version";
	
	private static bool IsOptionValue(string[] args, int index) =>
		index > 0 && args[index - 1] is "--flag" or "--target" or "--entry";
	
	private static Task<int> RunAsync(string[] args)
	{
		switch (args)
		{
			case ["--version"]:
				Console.WriteLine($"cella {SemanticVersion.Current}");
				return Task.FromResult(0);
			
			case ["build", .. var rest]:
				return BuildAsync(rest);
			
			case ["run", .. var rest]:
				return RunProgramAsync(rest);
			
			case ["new", .. var rest]:
				return Task.FromResult(ProjectTemplate.Create(rest));
			
			case [var command, ..] when !command.StartsWith('-'):
				Console.WriteLine($"Unknown command '{command}'");
				return Task.FromResult(1);
			
			default:
				Console.WriteLine(Usage);
				return Task.FromResult(1);
		}
	}
	
	private static async Task<int> BuildAsync(string[] args)
	{
		if (ParseBuild(args) is not var (options, sourcePath, showHints))
			return 1;
		
		var result = await new Builder(options, new ConsoleBuildOutput(Environment.CurrentDirectory, showHints))
			.BuildAsync(sourcePath);
		
		return result.Succeeded ? 0 : 1;
	}
	
	private static async Task<int> RunProgramAsync(string[] args)
	{
		var separator = Array.IndexOf(args, "--");
		var buildArgs = separator < 0 ? args : args[..separator];
		string[] programArgs = separator < 0 ? [] : args[(separator + 1)..];
		if (ParseBuild(buildArgs) is not var (options, sourcePath, showHints))
			return 1;
		
		var host = TargetTriple.FromHost();
		if (options.Target.Os != host.Os || options.Target.Arch != host.Arch)
		{
			var target = buildArgs[Array.IndexOf(buildArgs, "--target") + 1];
			Console.WriteLine($"Cannot run '{target}' programs on this system");
			return 1;
		}
		
		var output = new ConsoleBuildOutput(Environment.CurrentDirectory, showHints) { ReportsOutputs = false };
		var result = await new Builder(options with { RequiresProgram = true }, output).BuildAsync(sourcePath);
		if (!result.Succeeded || result.ProgramPath is not { } program)
			return 1;
		
		return await StartProgramAsync(program, programArgs);
	}
	
	private static async Task<int> StartProgramAsync(string path, string[] arguments)
	{
		var start = new ProcessStartInfo(path)
		{
			UseShellExecute = false,
			WorkingDirectory = Environment.CurrentDirectory
		};
		
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		
		Console.CancelKeyPress += KeepRunning;
		try
		{
			using var process = Process.Start(start)!;
			await process.WaitForExitAsync();
			return process.ExitCode;
		}
		catch (Win32Exception exception)
		{
			Console.WriteLine($"Couldn't start '{path}': {exception.Message}");
			return 1;
		}
		finally
		{
			Console.CancelKeyPress -= KeepRunning;
		}
	}
	
	private static void KeepRunning(object? sender, ConsoleCancelEventArgs e) => e.Cancel = true;
	
	private static (BuildOptions Options, string SourcePath, bool ShowHints)? ParseBuild(string[] args)
	{
		if (args.Where((arg, i) => arg.StartsWith("--") && !_options.Contains(arg) && !IsOptionValue(args, i))
			    .FirstOrDefault() is { } unknown)
		{
			Console.WriteLine($"Unknown option '{unknown}'");
			return null;
		}
		
		var verbose = args.Contains("--verbose");
		var showHints = args.Contains("--hints");
		var optimizeMode = args.Contains("--release") ? OptimizeMode.Release : OptimizeMode.Debug;
		if (ParseFlagArguments(args) is not { } flagOverrides)
			return null;
		
		if (ParseTarget(args) is not { } targetTriple)
			return null;
		
		if (!TryParseEntry(args, out var entry))
			return null;
		
		var paths = args.Where((arg, i) => !arg.StartsWith("--") && !IsOptionValue(args, i)).ToArray();
		if (paths is [_, var extra, ..])
		{
			Console.WriteLine($"Unexpected argument '{extra}'");
			return null;
		}
		
		var sourcePath = paths is [var path] ? path : ".";
		
		var options = new BuildOptions
		{
			Target = targetTriple,
			OptimizeMode = optimizeMode,
			FlagOverrides = flagOverrides,
			Verbose = verbose,
			EmitsIr = args.Contains("--emit-ir"),
			EmitsAssembly = args.Contains("--emit-asm"),
			Entry = entry
		};
		
		return (options, sourcePath, showHints);
	}
	
	private static bool TryParseEntry(string[] args, out string? entry)
	{
		entry = null;
		var index = Array.IndexOf(args, "--entry");
		if (index < 0)
			return true;
		
		if (index + 1 == args.Length || args[index + 1].StartsWith("--"))
		{
			Console.WriteLine("Expected a function after '--entry'");
			return false;
		}
		
		if (Array.IndexOf(args, "--entry", index + 1) >= 0)
		{
			Console.WriteLine("'--entry' is given more than once");
			return false;
		}
		
		entry = args[index + 1];
		return true;
	}
	
	private static TargetTriple? ParseTarget(string[] args)
	{
		var index = Array.IndexOf(args, "--target");
		if (index < 0)
			return TargetTriple.FromHost().WithDefaultAbi();
		
		if (index + 1 == args.Length || args[index + 1].StartsWith("--"))
		{
			Console.WriteLine("Expected a target after '--target'");
			return null;
		}
		
		if (Array.IndexOf(args, "--target", index + 1) >= 0)
		{
			Console.WriteLine("'--target' is given more than once");
			return null;
		}
		
		if (TargetTriple.Parse(args[index + 1]) is { } target)
		{
			if (target.Abi != TargetTriple.Abis.Msvc || TargetTriple.HasMsvc(target.Arch))
				return target.WithDefaultAbi();
			
			Console.WriteLine($"'{args[index + 1]}' needs Visual Studio's C++ build tools and the Windows SDK");
			return null;
		}
		
		Console.WriteLine($"Unknown target '{args[index + 1]}'");
		return null;
	}
	
	private static Dictionary<string, bool>? ParseFlagArguments(string[] args)
	{
		var flags = new Dictionary<string, bool>();
		for (var i = 0; i < args.Length; i++)
		{
			if (args[i] != "--flag")
				continue;
			
			if (++i == args.Length || args[i].StartsWith("--"))
			{
				Console.WriteLine("Expected a flag after '--flag'");
				return null;
			}
			
			var parts = args[i].Split('=', 2);
			if (parts[0].Length == 0)
			{
				Console.WriteLine("Expected a flag after '--flag'");
				return null;
			}
			
			if (parts is [_, not ("true" or "false")])
			{
				Console.WriteLine($"Expected 'true' or 'false' for flag '{parts[0]}'");
				return null;
			}
			
			flags[parts[0]] = parts is [_] or [_, "true"];
		}
		
		return flags;
	}
}