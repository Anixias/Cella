using Cella.Launcher;
using Xunit;

namespace Cella.Tests;

public sealed class LauncherTests : IDisposable
{
	private readonly string _directory =
		Path.Combine(Path.GetTempPath(), "cella-launcher-tests", Guid.NewGuid().ToString("N"));
	
	public LauncherTests() => Directory.CreateDirectory(_directory);
	
	public void Dispose() => Directory.Delete(_directory, true);
	
	private string Write(string relativePath, string text)
	{
		var path = Path.Combine(_directory, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
		return path;
	}
	
	[Fact]
	public void FindsTheHighestPinBelowAFolder()
	{
		var first = Write("app/app.celp", "OutputType = \"Executable\"\nCellaVersion = \"0.2.0\"\n");
		Write("lib/lib.celp", "OutputType = \"StaticLibrary\"\nCellaVersion = '0.10.0'\n");
		Write("tool/tool.celp", "OutputType = \"Executable\"\n\n[When.debug]\nCellaVersion = \"9.0.0\"\n");
		
		Assert.Equal("0.10.0", Pins.FindHighest(_directory)?.ToString());
		Assert.Equal("0.2.0", Pins.FindHighest(first)?.ToString());
	}
	
	[Fact]
	public void IgnoresProjectsWithoutAValidPin()
	{
		Write("app/app.celp", "OutputType = \"Executable\"\n");
		Write("bad/bad.celp", "OutputType = \"Executable\"\nCellaVersion = \"1.2\"\n");
		Assert.Null(Pins.FindHighest(_directory));
		Assert.Null(Pins.FindHighest(Path.Combine(_directory, "missing")));
	}
	
	[Fact]
	public void FindsTheProjectPathAmongArguments()
	{
		var project = Write("app/app.celp", "OutputType = \"Executable\"\n");
		Assert.Equal(project, Pins.FindProjectPath(["--release", "--target", "linux-x64", project]));
		Assert.Null(Pins.FindProjectPath(["--release", "missing.celp"]));
	}
}