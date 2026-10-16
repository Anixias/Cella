using System.Diagnostics;
using System.Text;

namespace Cella.Tests;

internal static class LinuxProcess
{
	private const string Shell = "sh";
	private const string Script = "exec 3>&2 2>/dev/null; (exec ./test \"$@\" 2>&3 3>&-)";
	
	private static readonly Lazy<bool> _isAvailable = new(FindAvailability);
	
	public static bool IsAvailable => _isAvailable.Value;
	
	public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string directory,
		IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "wsl.exe" : Shell)
		{
			WorkingDirectory = directory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		
		if (OperatingSystem.IsWindows())
		{
			start.ArgumentList.Add("-e");
			start.ArgumentList.Add(Shell);
		}
		
		start.ArgumentList.Add("-c");
		start.ArgumentList.Add(Script);
		start.ArgumentList.Add(Shell);
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		
		using var process = Process.Start(start)!;
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var error = process.StandardError.ReadToEndAsync(cancellationToken);
		
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(timeout);
		try
		{
			await process.WaitForExitAsync(limit.Token);
		}
		catch (OperationCanceledException)
		{
			process.Kill(true);
			throw new TimeoutException($"The program ran longer than {timeout.TotalSeconds} seconds");
		}
		
		return (process.ExitCode, await output, await error);
	}
	
	private static bool FindAvailability()
	{
		if (OperatingSystem.IsLinux())
			return true;
		
		if (!OperatingSystem.IsWindows())
			return false;
		
		try
		{
			using var process = Process.Start(new ProcessStartInfo("wsl.exe", ["-e", "true"])
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			})!;
			
			return process.WaitForExit(TimeSpan.FromSeconds(60)) && process.ExitCode == 0;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
		{
			return false;
		}
	}
}