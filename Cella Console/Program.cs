using Cella.Analysis.Text;

namespace CellaConsole;

public static class Program
{
	public static void Main(string[] args)
	{
		// Todo: Parse args using OptionSet
		
		var source = new StringBuffer("");
		var lexer = new Lexer(source);
		foreach (var token in lexer)
		{
			Console.WriteLine($"{token.Text}: {token.Type}");
		}
	}
}