using Cella.Compiler;
using Cella.Compiler.Building;
using Cella.Diagnostics;
using Xunit;

namespace Cella.Tests;

public sealed class BuilderTests : IDisposable
{
	private const string ValidSource = "mod test\n\nmain: fun -> i32 = 0\n";
	private const string InvalidSource = "mod test\n\nmain: fun -> i32 = true\n";
	
	private readonly string _directory =
		Path.Combine(Path.GetTempPath(), "cella-builder-tests", Guid.NewGuid().ToString("N"));
	
	private sealed class RecordingOutput : IBuildOutput
	{
		public List<string> Lines { get; } = [];
		public List<Diagnostic> Diagnostics { get; } = [];
		
		public void WriteLine(string text) => Lines.Add(text);
		public void WriteError(string text) => Lines.Add(text);
		public void Report(IEnumerable<Diagnostic> diagnostics) => Diagnostics.AddRange(diagnostics);
	}
	
	public BuilderTests()
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(Path.Combine(_directory, "test.celp"), "OutputType = \"Executable\"\n");
	}
	
	public void Dispose() => Directory.Delete(_directory, true);
	
	private string SourcePath => Path.Combine(_directory, "main.ce");
	
	private static BuildOptions CheckOptions(IReadOnlyDictionary<string, string>? openFiles = null) => new()
	{
		Target = TargetTriple.FromHost().WithDefaultAbi(Builder.ToolchainsDirectory),
		ChecksOnly = true,
		OpenFiles = openFiles ?? new Dictionary<string, string>()
	};
	
	private Task<BuildResult> CheckAsync(IBuildOutput output, IReadOnlyDictionary<string, string>? openFiles = null) =>
		CompilerThread.Run(() => new Builder(CheckOptions(openFiles), output).BuildAsync(_directory));
	
	[Fact]
	public async Task ChecksWithoutBuilding()
	{
		await File.WriteAllTextAsync(SourcePath, ValidSource, TestContext.Current.CancellationToken);
		var output = new RecordingOutput();
		var result = await CheckAsync(output);
		
		Assert.True(result.Succeeded);
		Assert.False(Directory.Exists(Path.Combine(_directory, "bin")));
		var project = Assert.Single(result.Projects);
		Assert.Equal("test.celp", Path.GetFileName(project.ProjectPath));
		Assert.Empty(project.Diagnostics);
		Assert.NotEmpty(project.Files);
		Assert.Empty(output.Lines);
	}
	
	[Fact]
	public async Task ReportsErrorsThroughOutput()
	{
		await File.WriteAllTextAsync(SourcePath, InvalidSource, TestContext.Current.CancellationToken);
		var output = new RecordingOutput();
		var result = await CheckAsync(output);
		
		Assert.False(result.Succeeded);
		var project = Assert.Single(result.Projects);
		var error = Assert.Single(project.Diagnostics);
		Assert.Equal(DiagnosticSeverity.Error, error.Severity);
		Assert.Equal(SourcePath, error.SourceLocation.Source.FilePath);
		Assert.Equal([error], output.Diagnostics);
	}
	
	[Fact]
	public async Task ReadsOpenFilesInsteadOfDisk()
	{
		await File.WriteAllTextAsync(SourcePath, ValidSource, TestContext.Current.CancellationToken);
		var result = await CheckAsync(new RecordingOutput(),
			new Dictionary<string, string> { [Path.GetFullPath(SourcePath)] = InvalidSource });
		
		Assert.False(result.Succeeded);
		Assert.Single(Assert.Single(result.Projects).Diagnostics);
		Assert.Equal(ValidSource, await File.ReadAllTextAsync(SourcePath, TestContext.Current.CancellationToken));
	}
}
