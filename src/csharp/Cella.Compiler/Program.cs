using Cella.Compiler.Building;
using Cella.Core.CodeGen;

namespace Cella.Compiler;

internal static class Program
{
	public static Task<int> Main(string[] args) => CompilerThread.Run(() => RunAsync(args));
	
	private static readonly string[] _options =
		["--verbose", "--hints", "--release", "--flag", "--target", "--emit-ir", "--emit-asm"];
	
	private static bool IsOptionValue(string[] args, int index) =>
		index > 0 && args[index - 1] is "--flag" or "--target";
	
	private static async Task<int> RunAsync(string[] args)
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
		
		var paths = args.Where((arg, i) => !arg.StartsWith("--") && !IsOptionValue(args, i)).ToArray();
		if (paths is [])
		{
			Console.WriteLine(
				"Usage: cella <project file or folder> [--release] [--verbose] [--hints] [--flag <name>[=true|false]] " +
				"[--target windows-x64|windows-x64-gnu|linux-x64] [--emit-ir] [--emit-asm]");
			
			return 1;
		}
		
		if (paths is [_, var extra, ..])
		{
			Console.WriteLine($"Unexpected argument '{extra}'");
			return 1;
		}
		
		var sourcePath = paths[0];
		var displayRoot = Directory.Exists(sourcePath)
			? Path.GetFullPath(sourcePath)
			: Path.GetDirectoryName(Path.GetFullPath(sourcePath));
		
		var options = new BuildOptions
		{
			Target = targetTriple,
			OptimizeMode = optimizeMode,
			FlagOverrides = flagOverrides,
			Verbose = verbose,
			EmitsIr = args.Contains("--emit-ir"),
			EmitsAssembly = args.Contains("--emit-asm")
		};
		
		var result = await new Builder(options, new ConsoleBuildOutput(displayRoot, showHints)).BuildAsync(sourcePath);
		return result.Succeeded ? 0 : 1;
	}
	
	private static TargetTriple? ParseTarget(string[] args)
	{
		var index = Array.IndexOf(args, "--target");
		if (index < 0)
			return TargetTriple.FromHost().WithDefaultAbi(Builder.ToolchainsDirectory);
		
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
			return target.WithDefaultAbi(Builder.ToolchainsDirectory);
		
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
