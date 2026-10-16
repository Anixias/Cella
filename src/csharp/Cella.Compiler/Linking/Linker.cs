using System.Diagnostics;
using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public sealed class Linker(string rootPath, bool verbose)
{
	public string? FindMissingLinker(LinkRequest request, Toolchain toolchain) =>
		File.Exists(GetLinkerPath(request, toolchain)) ? null : GetLinkerPath(request, toolchain);
	
	private string GetLinkerPath(LinkRequest request, Toolchain toolchain) => Path.Combine(rootPath,
		toolchain.GetTool(request.OutputType) + (OperatingSystem.IsWindows() ? ".exe" : ""));
	
	public async Task<(int ExitCode, string Errors)> LinkAsync(LinkRequest request, Toolchain toolchain)
	{
		if (request.OutputType == ProjectOutputType.StaticLibrary)
			File.Delete(request.OutputFile);
		
		var args = toolchain.GetLinkerArgs(request);
		
		if (verbose)
			Console.WriteLine($"Linker starting with arguments: {string.Join(" ", args)}");
		
		using var process = new Process();
		process.StartInfo.FileName = GetLinkerPath(request, toolchain);
		process.StartInfo.RedirectStandardError = true;
		process.StartInfo.UseShellExecute = false;
		
		foreach (var arg in args)
			process.StartInfo.ArgumentList.Add(arg);
		
		if (OperatingSystem.IsLinux())
			process.StartInfo.Environment["LD_LIBRARY_PATH"] = rootPath;
		
		var environmentVars = toolchain.GetLinkerEnvironmentVars();
		foreach (var (key, value) in environmentVars)
			process.StartInfo.Environment[key] = value;
		
		process.Start();
		var errors = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		return (process.ExitCode, await errors);
	}
}