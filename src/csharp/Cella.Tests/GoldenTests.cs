using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Cella.Compiler;
using Xunit;

namespace Cella.Tests;

public sealed class GoldenTests
{
	private const string UpdateVariable = "CELLA_UPDATE_GOLDEN";
	
	private static readonly string _casesDirectory = Path.Combine(GetSourceDirectory(), "Cases");
	private static readonly SemaphoreSlim _compilerLock = new(1, 1);
	private static readonly TimeSpan _runTimeout = TimeSpan.FromSeconds(10);
	
	public static TheoryData<string> Cases()
	{
		var cases = new TheoryData<string>();
		foreach (var path in Directory.EnumerateFiles(_casesDirectory, "*.ce", SearchOption.AllDirectories).Order())
			cases.Add(Path.GetRelativePath(_casesDirectory, path).Replace('\\', '/'));
		
		return cases;
	}
	
	[Theory]
	[MemberData(nameof(Cases))]
	public async Task Case(string name)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var sourcePath = Path.Combine(_casesDirectory, name);
		var expectedPath = Path.ChangeExtension(sourcePath, ".out");
		var actual = await RunAsync(sourcePath, cancellationToken);
		
		if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
		{
			await File.WriteAllTextAsync(expectedPath, actual, cancellationToken);
			return;
		}
		
		Assert.True(File.Exists(expectedPath),
			$"{name} has no expected output. Run with {UpdateVariable}=1 to record this output:\n\n{actual}");
		
		var expected = (await File.ReadAllTextAsync(expectedPath, cancellationToken)).ReplaceLineEndings("\n");
		Assert.Equal(expected, actual);
	}
	
	private static async Task<string> RunAsync(string sourcePath, CancellationToken cancellationToken)
	{
		var caseDirectory = Directory.CreateTempSubdirectory("cella-").FullName;
		try
		{
			File.Copy(sourcePath, Path.Combine(caseDirectory, Path.GetFileName(sourcePath)));
			var projectPath = Path.Combine(caseDirectory, "test.celp");
			await File.WriteAllTextAsync(projectPath,
				"OutputType = \"Executable\"\nSystemLinkPreference = \"Dynamic\"\n",
				cancellationToken);
			
			var (compileExitCode, compileOutput) = await CompileAsync(projectPath, cancellationToken);
			compileOutput = compileOutput.Replace(caseDirectory, "<case>");
			if (compileExitCode != 0)
				return Transcript(compileOutput, "[compilation failed]");
			
			var executablePath = Path.Combine(caseDirectory, "bin", OperatingSystem.IsWindows() ? "test.exe" : "test");
			var (exitCode, output, error) = await ExecuteAsync(executablePath, cancellationToken);
			var errorSection = error.Length > 0 ? $"[stderr]\n{error}" : "";
			return Transcript(compileOutput, output, errorSection, $"[exit code {exitCode}]");
		}
		finally
		{
			Directory.Delete(caseDirectory, true);
		}
	}
	
	private static async Task<(int ExitCode, string Output)> CompileAsync(string projectPath,
		CancellationToken cancellationToken)
	{
		await _compilerLock.WaitAsync(cancellationToken);
		var standardOutput = Console.Out;
		var standardError = Console.Error;
		try
		{
			var output = new StringWriter();
			Console.SetOut(output);
			Console.SetError(output);
			var exitCode = await Program.Main([projectPath, "--quiet"]);
			return (exitCode, output.ToString());
		}
		finally
		{
			Console.SetOut(standardOutput);
			Console.SetError(standardError);
			_compilerLock.Release();
		}
	}
	
	private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string executablePath,
		CancellationToken cancellationToken)
	{
		using var process = Process.Start(new ProcessStartInfo(executablePath)
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
}