using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Cella.Compiler;
using Xunit;

namespace Cella.Tests;

public sealed class GoldenTests
{
	private const string UpdateVariable = "CELLA_UPDATE_GOLDEN";
	
	private static readonly string _casesDirectory = Path.Combine(GetSourceDirectory(), "Cases");
	private static readonly TimeSpan _runTimeout = TimeSpan.FromSeconds(10);
	private static readonly AsyncLocal<TextWriter?> _compilerOutput = new();
	
	static GoldenTests()
	{
		Console.SetOut(new RoutingWriter(Console.Out));
		Console.SetError(new RoutingWriter(Console.Error));
	}
	
	public static TheoryData<string, bool> Cases()
	{
		var cases = new TheoryData<string, bool>();
		foreach (var path in FindCases())
		{
			var name = Path.GetRelativePath(_casesDirectory, path).Replace('\\', '/');
			cases.Add(name, false);
			if (name.StartsWith("run/"))
				cases.Add(name, true);
		}
		
		return cases;
	}
	
	private static IEnumerable<string> FindCases() => Directory.EnumerateDirectories(_casesDirectory)
		.SelectMany(static category => Directory.EnumerateFiles(category, "*.ce")
			.Concat(Directory.EnumerateDirectories(category)))
		.Order();
	
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task BuildsAreDeterministic(bool release)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var caseDirectory = Directory.CreateTempSubdirectory("cella-").FullName;
		try
		{
			CopySources(Path.Combine(_casesDirectory, "run", "library_dependencies"), caseDirectory);
			var projectPath = Path.Combine(caseDirectory, "test.celp");
			var first = await BuildOutputsAsync(projectPath, caseDirectory, release, cancellationToken);
			foreach (var output in Directory.EnumerateDirectories(caseDirectory, "*", SearchOption.AllDirectories)
				         .Where(static directory => Path.GetFileName(directory) is "bin" or "obj")
				         .ToList())
				Directory.Delete(output, true);
			
			var second = await BuildOutputsAsync(projectPath, caseDirectory, release, cancellationToken);
			Assert.NotEmpty(first);
			Assert.Equal(first, second);
		}
		finally
		{
			await DeleteDirectoryAsync(caseDirectory);
		}
	}
	
	private static async Task<Dictionary<string, string>> BuildOutputsAsync(string projectPath, string caseDirectory,
		bool release, CancellationToken cancellationToken)
	{
		var (exitCode, output) = await CompileAsync(projectPath, release, [], cancellationToken);
		Assert.True(exitCode == 0, output);
		return Directory.EnumerateFiles(caseDirectory, "*", SearchOption.AllDirectories)
			.Where(static path => Path.GetExtension(path) is ".exe" or ".dll" or ".lib" or ".pdb")
			.ToDictionary(path => Path.GetRelativePath(caseDirectory, path),
				static path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
	}
	
	[Theory]
	[MemberData(nameof(Cases))]
	public async Task Case(string name, bool release)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var updating = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
		var sourcePath = Path.Combine(_casesDirectory, name);
		var releasePath = Path.ChangeExtension(sourcePath, ".release.out");
		var hasReleaseOutput = release && File.Exists(releasePath);
		if (updating && release && !hasReleaseOutput)
			return;
		
		var expectedPath = hasReleaseOutput ? releasePath : Path.ChangeExtension(sourcePath, ".out");
		var actual = await RunAsync(sourcePath, release, cancellationToken);
		
		if (updating)
		{
			await File.WriteAllTextAsync(expectedPath, actual, cancellationToken);
			return;
		}
		
		Assert.True(File.Exists(expectedPath),
			$"{name} has no expected output. Run with {UpdateVariable}=1 to record this output:\n\n{actual}");
		
		var expected = (await File.ReadAllTextAsync(expectedPath, cancellationToken)).ReplaceLineEndings("\n");
		Assert.Equal(expected, actual);
	}
	
	private static async Task<string> RunAsync(string sourcePath, bool release, CancellationToken cancellationToken)
	{
		var caseDirectory = Directory.CreateTempSubdirectory("cella-").FullName;
		try
		{
			CopySources(sourcePath, caseDirectory);
			var projectPath = Path.Combine(caseDirectory, "test.celp");
			if (!File.Exists(projectPath))
				await File.WriteAllTextAsync(projectPath,
					"OutputType = \"Executable\"\nSystemLinkPreference = \"Dynamic\"\n",
					cancellationToken);
			
			var optionsPath = Path.ChangeExtension(sourcePath, ".options");
			string[] options = File.Exists(optionsPath)
				? await File.ReadAllLinesAsync(optionsPath, cancellationToken)
				: [];
			
			var (compileExitCode, compileOutput) = await CompileAsync(projectPath, release, options, cancellationToken);
			compileOutput = compileOutput.Replace(caseDirectory, "<case>");
			if (compileExitCode != 0)
				return Transcript(compileOutput, "[compilation failed]");
			
			var executablePath = Path.Combine(caseDirectory, "bin", OperatingSystem.IsWindows() ? "test.exe" : "test");
			var argumentsPath = Path.ChangeExtension(sourcePath, ".args");
			string[] arguments = File.Exists(argumentsPath)
				? await File.ReadAllLinesAsync(argumentsPath, cancellationToken)
				: [];
			
			var (exitCode, output, error) = await ExecuteAsync(executablePath, arguments, cancellationToken);
			var errorSection = error.Length > 0 ? $"[stderr]\n{error}" : "";
			return Transcript(compileOutput, output, errorSection, $"[exit code {exitCode}]");
		}
		finally
		{
			await DeleteDirectoryAsync(caseDirectory);
		}
	}
	
	private static async Task DeleteDirectoryAsync(string path)
	{
		for (var attempt = 1;; attempt++)
		{
			try
			{
				Directory.Delete(path, true);
				return;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 50)
			{
				await Task.Delay(100);
			}
		}
	}
	
	private static void CopySources(string sourcePath, string caseDirectory)
	{
		if (File.Exists(sourcePath))
		{
			File.Copy(sourcePath, Path.Combine(caseDirectory, Path.GetFileName(sourcePath)));
			return;
		}
		
		var files = Directory.EnumerateFiles(sourcePath, "*.ce", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(sourcePath, "*.celp", SearchOption.AllDirectories));
		
		foreach (var file in files)
		{
			var target = Path.Combine(caseDirectory, Path.GetRelativePath(sourcePath, file));
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(file, target);
		}
	}
	
	private static async Task<(int ExitCode, string Output)> CompileAsync(string projectPath, bool release,
		string[] options, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var output = new StringWriter();
		_compilerOutput.Value = output;
		string[] arguments = release
			? [projectPath, "--hints", "--release", ..options]
			: [projectPath, "--hints", ..options];
		
		var exitCode = await Program.Main(arguments);
		return (exitCode, output.ToString());
	}
	
	private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string executablePath,
		IEnumerable<string> arguments, CancellationToken cancellationToken)
	{
		if (OperatingSystem.IsWindows())
			return await WindowsProcess.RunAsync(executablePath, arguments, _runTimeout, cancellationToken);
		
		using var process = Process.Start(new ProcessStartInfo(executablePath, arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		})!;
		
		var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var error = process.StandardError.ReadToEndAsync(cancellationToken);
		
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_runTimeout);
		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			process.Kill(true);
			throw new TimeoutException($"The program ran longer than {_runTimeout.TotalSeconds} seconds");
		}
		
		return (process.ExitCode, await output, await error);
	}
	
	private static string Transcript(params string[] parts)
	{
		var builder = new StringBuilder();
		foreach (var part in parts)
		{
			var text = part.ReplaceLineEndings("\n");
			if (text.Length == 0)
				continue;
			
			builder.Append(text);
			if (!text.EndsWith('\n'))
				builder.Append('\n');
		}
		
		return builder.ToString();
	}
	
	private static string GetSourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
	
	private sealed class RoutingWriter(TextWriter fallback) : TextWriter
	{
		private TextWriter Target => _compilerOutput.Value ?? fallback;
		
		public override Encoding Encoding => fallback.Encoding;
		
		public override void Write(char value) => Target.Write(value);
		
		public override void Write(string? value) => Target.Write(value);
		
		public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);
		
		public override void Flush() => Target.Flush();
	}
}