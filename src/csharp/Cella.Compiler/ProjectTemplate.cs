using Cella.Compiler.Projects;
using Cella.Core.Text;

namespace Cella.Compiler;

internal static class ProjectTemplate
{
	private static readonly string[] _options = ["--lib", "--gitignore"];
	
	public static int Create(string[] args)
	{
		if (args.FirstOrDefault(static arg => arg.StartsWith('-') && !_options.Contains(arg)) is { } unknown)
		{
			Console.WriteLine($"Unknown option '{unknown}'");
			return 1;
		}
		
		var names = args.Where(static arg => !arg.StartsWith('-')).ToArray();
		if (names is [_, var extra, ..])
		{
			Console.WriteLine($"Unexpected argument '{extra}'");
			return 1;
		}
		
		var directory = Path.TrimEndingDirectorySeparator(
			names is [var given] ? Path.GetFullPath(given) : Environment.CurrentDirectory);
		
		var name = Path.GetFileName(directory);
		if (!IsIdentifier(name))
		{
			Console.WriteLine($"'{name}' can't be a project name");
			return 1;
		}
		
		if (names is [var folder] && (File.Exists(directory) ||
		                              Directory.Exists(directory) &&
		                              Directory.EnumerateFileSystemEntries(directory).Any()))
		{
			Console.WriteLine($"'{folder}' already exists");
			return 1;
		}
		
		if (Directory.Exists(directory) &&
		    Directory.EnumerateFiles(directory, "*.celp").FirstOrDefault() is { } project)
		{
			Console.WriteLine($"'{Path.GetFileName(project)}' already exists");
			return 1;
		}
		
		var isLibrary = args.Contains("--lib");
		var files = new List<(string Name, string Text)>
		{
			($"{name}.celp", DescribeProject(isLibrary)),
			isLibrary ? ($"{name}.ce", DescribeLibrary(name)) : ("main.ce", DescribeProgram(name))
		};
		
		if (args.Contains("--gitignore"))
			files.Add((".gitignore", "bin/\nobj/\n"));
		
		if (files.FirstOrDefault(file => File.Exists(Path.Combine(directory, file.Name))) is { Name: { } taken })
		{
			Console.WriteLine($"'{taken}' already exists");
			return 1;
		}
		
		Directory.CreateDirectory(directory);
		foreach (var (fileName, text) in files)
			File.WriteAllText(Path.Combine(directory, fileName), text);
		
		Console.WriteLine($"Created '{name}'");
		return 0;
	}
	
	private static bool IsIdentifier(string name) =>
		new FilteredScanner(new StringSource(name, name)).ToArray() is
			[{ Type: TokenType.Identifier } token, { Type: TokenType.EndOfFile }] && token.Text == name;
	
	private static string DescribeProject(bool isLibrary) =>
		$"OutputType = \"{(isLibrary ? "StaticLibrary" : "Executable")}\"\n" +
		$"CellaVersion = \"{SemanticVersion.Current}\"\n";
	
	private static string DescribeProgram(string name) =>
		$"mod {name}\n" +
		"\n" +
		"ext\n" +
		"{\n" +
		"\tputs: fun(text: cstr) -> i32\n" +
		"}\n" +
		"\n" +
		"main: fun -> i32\n" +
		"{\n" +
		"\tputs(\"Hello, World!\")\n" +
		"\tret 0\n" +
		"}\n";
	
	private static string DescribeLibrary(string name) =>
		$"mod {name}\n" +
		"\n" +
		"add: pub fun(a: i32, b: i32) -> i32 => a + b\n";
}