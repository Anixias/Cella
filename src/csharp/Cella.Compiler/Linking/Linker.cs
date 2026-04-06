using System.Diagnostics;
using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public sealed class Linker(string rootPath)
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
		
		var argStr = string.Join(" ", args);
		Console.WriteLine($"Linker starting with arguments: {argStr}");
		
		var linkerPath = Path.Combine(rootPath, linkerName);
		
		var process = new Process
		{
			StartInfo =
			{
				FileName = linkerPath,
				Arguments = argStr,
				RedirectStandardError = true,
				UseShellExecute = false
			}
		};
		
		var environmentVars = toolchain.GetLinkerEnvironmentVars(request);
		foreach (var (key, value) in environmentVars)
			process.StartInfo.Environment[key] = value;
		
		process.Start();
		
		await process.WaitForExitAsync();
		
		if (process.ExitCode != 0)
			await Console.Error.WriteLineAsync(await process.StandardError.ReadToEndAsync());
		
		return process.ExitCode;
	}
}