using Cella.Analysis.Text;

namespace CellaConsole;

public static class Program
{
	public static void Main(string[] args)
	{
		// Todo: Parse args using OptionSet
		
		var source = new StringBuffer("main: entry(args: String[]): Int32\n{\n\tret 0\n}");
		var lexer = new FilteredLexer(source);
		foreach (var token in lexer)
		{
			Console.WriteLine($"{token.Text}: {token.Type}");
		}
	}
}