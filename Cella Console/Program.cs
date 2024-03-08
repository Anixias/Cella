using Cella.Analysis.Syntax;
using Cella.Analysis.Text;

namespace CellaConsole;

public static class Program
{
	public static void Main(string[] args)
	{
		// Todo: Parse args using OptionSet
		
		var source = new StringBuffer("main: entry(args: String[]): Int32\n{\n\tret 0\n}");
		var lexer = new FilteredLexer(source);
		var ast = Parser.Parse(lexer);
		if (ast is null)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine("Failed to compile");
			return;
		}

		AstPrinter.Print(ast, Console.Out);
	}
}