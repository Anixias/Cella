using System.Diagnostics;
using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public sealed class Linker(string rootPath, bool verbose)
{
	public async Task<int> LinkAsync(LinkRequest request, Toolchain toolchain)
	{
		// TODO Only works on Windows host
		string linkerName;
		bool addFlavor;
		switch (request.OutputType)
		{
			case ProjectOutputType.StaticLibrary:
				linkerName = "llvm-lib.exe";
				addFlavor = false;
				break;
			
			default:
				linkerName = "lld.exe";
				addFlavor = true;
				break;
		}
		
		var args = new List<string>();
		
		if (addFlavor)
		{
			args.Add("-flavor");
			args.Add(toolchain.LldFlavor);
		}
		
		args.AddRange(toolchain.GetLinkerArgs(request));
		
		if (verbose)
			Console.WriteLine($"Linker starting with arguments: {string.Join(" ", args)}");
		
		var linkerPath = Path.Combine(rootPath, linkerName);
		
		using var process = new Process();
		process.StartInfo.FileName = linkerPath;
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