using Cella.Compiler;

namespace Cella.LanguageServer;

internal static class Program
{
	public static async Task<int> Main()
	{
		Installation.UseComponentLibraries();
		var output = Console.OpenStandardOutput();
		Console.SetOut(Console.Error);
		return await new Server(Console.OpenStandardInput(), output, TimeSpan.FromMilliseconds(250)).RunAsync();
	}
}