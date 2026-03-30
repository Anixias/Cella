using System.Diagnostics;

namespace Cella.Compiler.Linking;

public sealed class Linker(string lldPath)
{
	public async Task<int> LinkAsync(LinkRequest request, Toolchain toolchain)
	{
		var args = new List<string> { "-flavor", toolchain.LldFlavor };
		args.AddRange(toolchain.GetLinkerArgs(request));
		
		var argStr = string.Join(" ", args);
		Console.WriteLine($"Linker starting with arguments: {argStr}");
		
		var process = new Process();
		process.StartInfo.FileName = lldPath;
		process.StartInfo.Arguments = argStr;
		process.StartInfo.RedirectStandardError = true;
		process.StartInfo.UseShellExecute = false;
		process.Start();
		
		await process.WaitForExitAsync();
		
		if (process.ExitCode != 0)
			await Console.Error.WriteLineAsync(await process.StandardError.ReadToEndAsync());
		
		return process.ExitCode;
	}
}