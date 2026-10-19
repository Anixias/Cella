namespace Cella.Compiler;

public static class CommandLine
{
	private static readonly string[] _valueOptions = ["--flag", "--target", "--entry"];
	
	public static bool IsOptionValue(IReadOnlyList<string> args, int index) =>
		index > 0 && _valueOptions.Contains(args[index - 1]);
	
	public static IEnumerable<string> FindPaths(IReadOnlyList<string> args) =>
		args.Where((arg, i) => !arg.StartsWith("--") && !IsOptionValue(args, i));
}