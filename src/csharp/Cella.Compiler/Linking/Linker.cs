using System.Diagnostics;
using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public sealed class Linker(string rootPath, bool verbose)
{
	public string? FindMissingLinker(LinkRequest request) =>
		File.Exists(GetLinkerPath(request)) ? null : GetLinkerPath(request);
	
	private string GetLinkerPath(LinkRequest request) => Path.Combine(rootPath,
		request.OutputType == ProjectOutputType.StaticLibrary ? "llvm-lib.exe" : "lld.exe");
	
	public async Task<int> LinkAsync(LinkRequest request, Toolchain toolchain)
	{
		// TODO Only works on Windows host
		var args = new List<string>();
		
		if (request.OutputType != ProjectOutputType.StaticLibrary)
		{
			args.Add("-flavor");
			args.Add(toolchain.LldFlavor);
		}
		
		args.AddRange(toolchain.GetLinkerArgs(request));
		
		if (verbose)
			Console.WriteLine($"Linker starting with arguments: {string.Join(" ", args)}");
		
		using var process = new Process();
		process.StartInfo.FileName = GetLinkerPath(request);
		process.StartInfo.RedirectStandardError = true;
		process.StartInfo.UseShellExecute = false;
		
		foreach (var arg in args)
			process.StartInfo.ArgumentList.Add(arg);
		
		var environmentVars = toolchain.GetLinkerEnvironmentVars();
		foreach (var (key, value) in environmentVars)
			process.StartInfo.Environment[key] = value;
		
		process.Start();
		var errors = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		
		if (process.ExitCode != 0)
			await Console.Error.WriteLineAsync(await errors);
		
		return process.ExitCode;
	}
}