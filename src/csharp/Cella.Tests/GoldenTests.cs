using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cella.Compiler;
using Xunit;

namespace Cella.Tests;

public sealed class GoldenTests
{
	private const string UpdateVariable = "CELLA_UPDATE_GOLDEN";
	private const string ClangVariable = "CELLA_TEST_CLANG";
	
	private static readonly string _casesDirectory = Path.Combine(GetSourceDirectory(), "Cases");
	private static readonly TimeSpan _runTimeout = TimeSpan.FromSeconds(10);
	private static readonly Regex _exitCodeLine = new(@"^\[exit code (-?\d+)\]$", RegexOptions.Multiline);
	
	private static readonly Regex _displayedPath =
		new(@"<case>[^\s:']*|(?<=^====== ).+(?= ======$)", RegexOptions.Multiline);
	
	private static readonly AsyncLocal<TextWriter?> _compilerOutput = new();
	
	static GoldenTests()
	{
		Console.SetOut(new RoutingWriter(Console.Out));
		Console.SetError(new RoutingWriter(Console.Error));
	}
	
	private static readonly string[] _targets = [WindowsTarget, WindowsGnuTarget, LinuxTarget];
	
	private const string WindowsTarget = "windows-x64";
	private const string WindowsGnuTarget = "windows-x64-gnu";
	private const string LinuxTarget = "linux-x64";
	
	public static TheoryData<string, bool, string> Cases()
	{
		var cases = new TheoryData<string, bool, string>();
		foreach (var path in FindCases())
		{
			var name = Path.GetRelativePath(_casesDirectory, path).Replace('\\', '/');
			foreach (var target in _targets.Where(target => RunsOn(path, target)))
			{
				cases.Add(name, false, target);
				if (name.StartsWith("run/"))
					cases.Add(name, true, target);
			}
		}
		
		return cases;
	}
	
	private static string GetSystem(string target) => target.Split('-')[0];
	
	private static bool RunsOn(string sourcePath, string target)
	{
		var directory = Path.GetDirectoryName(sourcePath)!;
		var prefix = Path.GetFileNameWithoutExtension(sourcePath) + ".";
		var outputs = Directory.EnumerateFiles(directory, prefix + "*out")
			.Select(static output => Path.GetFileName(output))
			.Where(output => output.Length > prefix.Length)
			.Select(output => output[prefix.Length..])
			.ToList();
		
		return outputs.Count == 0 || outputs.Any(output => output is "out" or "release.out" ||
		                                                   output.StartsWith(GetSystem(target) + ".",
			                                                   StringComparison.Ordinal));
	}
	
	private static IEnumerable<string> FindExpectedPaths(string sourcePath, bool release, string target)
	{
		var system = GetSystem(target);
		if (release)
		{
			yield return Path.ChangeExtension(sourcePath, $".{system}.release.out");
			yield return Path.ChangeExtension(sourcePath, ".release.out");
		}
		
		yield return Path.ChangeExtension(sourcePath, $".{system}.out");
		yield return Path.ChangeExtension(sourcePath, ".out");
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
			.Where(static path => Path.GetFileName(Path.GetDirectoryName(path)) == "bin")
			.ToDictionary(path => Path.GetRelativePath(caseDirectory, path),
				static path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
	}
	
	[Theory]
	[MemberData(nameof(Cases))]
	public async Task Case(string name, bool release, string target)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var updating = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
		var sourcePath = Path.Combine(_casesDirectory, name);
		var hostPath = Path.ChangeExtension(sourcePath, ".host");
		if (File.Exists(hostPath) && (await File.ReadAllTextAsync(hostPath, cancellationToken)).Trim() is var host &&
		    !OperatingSystem.IsOSPlatform(host))
			Assert.Skip($"{name} only builds on {host}");
		
		var candidates = FindExpectedPaths(sourcePath, release, target).ToList();
		var expectedPath = candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
		var isOwnOutput = Path.GetFileName(expectedPath).Contains($".{GetSystem(target)}.") ||
		                  target == WindowsTarget;
		
		if (updating && (!isOwnOutput || release && !expectedPath.EndsWith(".release.out", StringComparison.Ordinal)))
			return;
		
		var actual = await RunAsync(sourcePath, release, target, cancellationToken);
		
		if (updating)
		{
			await File.WriteAllTextAsync(expectedPath, actual, cancellationToken);
			return;
		}
		
		Assert.True(File.Exists(expectedPath),
			$"{name} has no expected output. Run with {UpdateVariable}=1 to record this output:\n\n{actual}");
		
		var expected = (await File.ReadAllTextAsync(expectedPath, cancellationToken)).ReplaceLineEndings("\n");
		if (target != LinuxTarget && !OperatingSystem.IsWindows())
			expected = _exitCodeLine.Replace(expected,
				static match => $"[exit code {int.Parse(match.Groups[1].Value) & 0xFF}]");
		
		Assert.Equal(expected, actual);
	}
	
	private static async Task<string> RunAsync(string sourcePath, bool release, string target,
		CancellationToken cancellationToken)
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
			
			await BuildCSourcesAsync(caseDirectory, target, cancellationToken);
			var (compileExitCode, compileOutput) =
				await CompileAsync(projectPath, release, [..options, "--target", target], cancellationToken);
			
			compileOutput = compileOutput.Replace(caseDirectory, "<case>");
			if (!OperatingSystem.IsWindows())
				compileOutput = _displayedPath.Replace(compileOutput, static match => match.Value.Replace('/', '\\'));
			
			if (compileExitCode != 0)
				return Transcript(compileOutput, "[compilation failed]");
			
			var argumentsPath = Path.ChangeExtension(sourcePath, ".args");
			string[] arguments = File.Exists(argumentsPath)
				? await File.ReadAllLinesAsync(argumentsPath, cancellationToken)
				: [];
			
			var binDirectory = Path.Combine(caseDirectory, "bin");
			if (target == LinuxTarget && !LinuxProcess.IsAvailable)
				Assert.Skip("Linux programs can't run on this machine (install WSL to run them on Windows)");
			
			if (target != LinuxTarget && !CanRunWindowsPrograms)
				Assert.Skip("Windows programs can't run on this machine");
			
			var (exitCode, output, error) = target == LinuxTarget
				? await LinuxProcess.RunAsync(binDirectory, arguments, _runTimeout, cancellationToken)
				: await ExecuteAsync(Path.Combine(binDirectory, "test.exe"), arguments, cancellationToken);
			
			var errorSection = error.Length > 0 ? $"[stderr]\n{error}" : "";
			return Transcript(compileOutput, output, errorSection, $"[exit code {exitCode}]");
		}
		finally
		{
			await DeleteDirectoryAsync(caseDirectory);
		}
	}
	
	private static async Task BuildCSourcesAsync(string caseDirectory, string target,
		CancellationToken cancellationToken)
	{
		var sources = Directory.GetFiles(caseDirectory, "*.c", SearchOption.AllDirectories);
		if (sources.Length == 0)
			return;
		
		var clang = FindClang();
		if (clang is null)
			Assert.Skip($"Building C sources needs clang on PATH or in {ClangVariable}");
		
		var triple = TargetTriple.Parse(target)!.Value
			.WithDefaultAbi(Path.Combine(AppContext.BaseDirectory, "toolchains"));
		
		string[] flags = triple.IsElf ? ["-fPIC"] : [];
		var archiver = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "llvm-ar.exe" : "llvm-ar");
		foreach (var source in sources)
		{
			var objectPath = Path.ChangeExtension(source, ".o");
			await RunToolAsync(clang,
			[
				"-c", "-O1", "-fno-stack-protector", $"--target={triple.ToLlvm()}", ..flags,
				"-o", objectPath, source
			], cancellationToken);
			
			var name = Path.GetFileNameWithoutExtension(source);
			var library = Path.Combine(Path.GetDirectoryName(source)!, triple.IsElf ? $"lib{name}.a" : $"{name}.lib");
			await RunToolAsync(archiver, ["rcs", library, objectPath], cancellationToken);
		}
	}
	
	private static string? FindClang()
	{
		if (Environment.GetEnvironmentVariable(ClangVariable) is { Length: > 0 } configured)
			return configured;
		
		var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Select(static directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "clang.exe" : "clang"));
		
		if (OperatingSystem.IsWindows())
			candidates = candidates.Append(Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "clang.exe"));
		
		return candidates.FirstOrDefault(File.Exists);
	}
	
	private static async Task RunToolAsync(string tool, IEnumerable<string> arguments,
		CancellationToken cancellationToken)
	{
		var start = new ProcessStartInfo(tool, arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		
		if (OperatingSystem.IsLinux())
			start.Environment["LD_LIBRARY_PATH"] = AppContext.BaseDirectory;
		
		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var error = process.StandardError.ReadToEndAsync(cancellationToken);
		await process.WaitForExitAsync(cancellationToken);
		Assert.True(process.ExitCode == 0, $"{Path.GetFileName(tool)} failed:\n{await output}{await error}");
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
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				if (attempt == 50)
					return;
				
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
			.Concat(Directory.EnumerateFiles(sourcePath, "*.celp", SearchOption.AllDirectories))
			.Concat(Directory.EnumerateFiles(sourcePath, "*.c", SearchOption.AllDirectories));
		
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
	
	private static string GetSourceDirectory([CallerFilePath] string path = "") =>
		Path.GetDirectoryName(OperatingSystem.IsWindows() || path.StartsWith('/') ? path : ToWslPath(path))!;
	
	private static string ToWslPath(string windowsPath) =>
		$"/mnt/{char.ToLowerInvariant(windowsPath[0])}{windowsPath[2..].Replace('\\', '/')}";
	
	private static bool CanRunWindowsPrograms => OperatingSystem.IsWindows() ||
	                                             Directory.Exists(InteropDirectory) && Directory
		                                             .EnumerateFiles(InteropDirectory, "WSLInterop*").Any();
	
	private const string InteropDirectory = "/proc/sys/fs/binfmt_misc";
	
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