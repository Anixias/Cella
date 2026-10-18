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
		if (args.Where((arg, i) => arg.StartsWith("--") && !_options.Contains(arg) && !IsOptionValue(args, i))
			    .FirstOrDefault() is { } unknown)
		{
			Console.WriteLine($"Unknown option '{unknown}'");
			return 1;
		}
		
		var verbose = args.Contains("--verbose");
		var showHints = args.Contains("--hints");
		var optimizeMode = args.Contains("--release") ? OptimizeMode.Release : OptimizeMode.Debug;
		if (ParseFlagArguments(args) is not { } flagOverrides)
			return 1;
		
		if (ParseTarget(args) is not { } targetTriple)
			return 1;
		
		if (!TryParseEntry(args, out var entry))
			return 1;
		
		var paths = args.Where((arg, i) => !arg.StartsWith("--") && !IsOptionValue(args, i)).ToArray();
		if (paths is [_, var extra, ..])
		{
			Console.WriteLine($"Unexpected argument '{extra}'");
			return 1;
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
		
		var result =
			await new Builder(options, new ConsoleBuildOutput(Environment.CurrentDirectory, showHints))
				.BuildAsync(sourcePath);
		
		return result.Succeeded ? 0 : 1;
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