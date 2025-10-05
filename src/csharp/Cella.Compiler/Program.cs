using Cella.Core.Text;

namespace Cella.Compiler;

internal static class Program
{
	public static void Main(string[] args)
	{
		if (args.Length == 0)
			return;
		
		// TEMP
		var sourceFile = args[0];
		if (!File.Exists(sourceFile))
			return;
		
		// TODO Create some kind of StreamSource so the entire file doesn't have to be loaded into memory
		var sourceString = File.ReadAllText(sourceFile);
		var source = new StringSource(sourceString);
		var scanner = new FilteredScanner(source);
		
		foreach (var token in scanner)
			Console.WriteLine(token);
	}
}