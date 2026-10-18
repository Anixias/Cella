using System.Diagnostics;
using Cella.Compiler.Projects;
using Xunit;

namespace Cella.Tests;

public sealed class CommandLineTests : IDisposable
{
	private readonly string _directory = Directory.CreateTempSubdirectory("cella-cli-").FullName;
	
	public void Dispose() => Directory.Delete(_directory, true);
	
	private static CancellationToken Token => TestContext.Current.CancellationToken;
	
	private static string CompilerPath =>
		Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "cella.exe" : "cella");
	
	private static async Task<(int ExitCode, string Output)> CellaAsync(string workingDirectory,
		params string[] arguments)
	{
		var start = new ProcessStartInfo(CompilerPath)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = workingDirectory
		};
		
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		
		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEndAsync(Token);
		var error = process.StandardError.ReadToEndAsync(Token);
		await process.WaitForExitAsync(Token);
		return (process.ExitCode, (await output + await error).ReplaceLineEndings("\n"));
	}
	
	private string[] ListFiles(string folder) =>
	[
		..Directory.EnumerateFileSystemEntries(Path.Combine(_directory, folder))
			.Select(static path => Path.GetFileName(path))
			.Order(StringComparer.Ordinal)
	];
	
	[Fact]
	public async Task CreatesAndRunsPrograms()
	{
		Assert.Equal((0, "Created 'game'\n"), await CellaAsync(_directory, "new", "game"));
		Assert.Equal(["game.celp", "main.ce"], ListFiles("game"));
		Assert.Equal($"OutputType = \"Executable\"\nCellaVersion = \"{SemanticVersion.Current}\"\n",
			await File.ReadAllTextAsync(Path.Combine(_directory, "game", "game.celp"), Token));
		
		Assert.Equal((0, "Hello, World!\n"), await CellaAsync(Path.Combine(_directory, "game"), "run"));
	}
	
	[Fact]
	public async Task CreatesLibraries()
	{
		Assert.Equal((0, "Created 'physics'\n"),
			await CellaAsync(_directory, "new", "physics", "--lib", "--gitignore"));
		
		Assert.Equal([".gitignore", "physics.ce", "physics.celp"], ListFiles("physics"));
		Assert.Equal("bin/\nobj/\n",
			await File.ReadAllTextAsync(Path.Combine(_directory, "physics", ".gitignore"), Token));
		
		Assert.Equal((1, "'physics' is a library\n"), await CellaAsync(_directory, "run", "physics"));
	}
	
	[Fact]
	public async Task SetsUpTheCurrentFolder()
	{
		var folder = Directory.CreateDirectory(Path.Combine(_directory, "tool")).FullName;
		Assert.Equal((0, "Created 'tool'\n"), await CellaAsync(folder, "new"));
		Assert.Equal(["main.ce", "tool.celp"], ListFiles("tool"));
		Assert.Equal((1, "'tool.celp' already exists\n"), await CellaAsync(folder, "new"));
	}
	
	[Fact]
	public async Task RejectsUnusableProjectNames()
	{
		Directory.CreateDirectory(Path.Combine(_directory, "taken", "inner"));
		Assert.Equal((1, "'my-game' can't be a project name\n"), await CellaAsync(_directory, "new", "my-game"));
		Assert.Equal((1, "'taken' already exists\n"), await CellaAsync(_directory, "new", "taken"));
		Assert.Equal((1, "Unknown option '--shared'\n"), await CellaAsync(_directory, "new", "lib", "--shared"));
	}
	
	[Fact]
	public async Task PassesArgumentsAndExitCodes()
	{
		var folder = Directory.CreateDirectory(Path.Combine(_directory, "echo")).FullName;
		await File.WriteAllTextAsync(Path.Combine(folder, "echo.celp"), "OutputType = \"Executable\"\n", Token);
		await File.WriteAllTextAsync(Path.Combine(folder, "main.ce"), """
		                                                              mod echo
		                                                              
		                                                              ext
		                                                              {
		                                                              	printf: fun(format: cstr, ...) -> i32
		                                                              }
		                                                              
		                                                              main: fun(args: ptr[str], count: usize) -> i32
		                                                              {
		                                                              	for i in 1..count
		                                                              		printf("%.*s\n", i32(args[i].byteLength), args[i].data)
		                                                              
		                                                              	ret 7
		                                                              }
		                                                              
		                                                              """, Token);
		
		Assert.Equal((7, "two words\n--release\n"),
			await CellaAsync(_directory, "run", "echo", "--", "two words", "--release"));
	}
	
	[Fact]
	public async Task RunsOnlyProgramsForThisSystem()
	{
		var other = OperatingSystem.IsWindows() ? "linux-x64" : "windows-x64";
		await CellaAsync(_directory, "new", "game");
		Assert.Equal((1, $"Cannot run '{other}' programs on this system\n"),
			await CellaAsync(_directory, "run", "game", "--target", other));
	}
	
	[Fact]
	public async Task AsksWhichProgramToRun()
	{
		await CellaAsync(_directory, "new", "first");
		await CellaAsync(_directory, "new", "second");
		var programs = $"'{Path.Combine("first", "first.celp")}', '{Path.Combine("second", "second.celp")}'";
		Assert.Equal((1, $"'.' has several programs: {programs}\n"), await CellaAsync(_directory, "run"));
	}
}