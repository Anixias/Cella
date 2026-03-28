using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Syntax;
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
		
		// @TEMP
		foreach (var token in scanner)
			Console.WriteLine(token);
		
		Console.WriteLine("\n=== AST ===");
		
		// TODO Change parser to retrieve tokens as needed
		var tokens = scanner.ToImmutableArray();
		var parser = new FileParser(tokens);
		var ast = parser.Parse();
		
		Console.WriteLine(ast is null ? "Failed to parse." : AstPrinter.Print(ast));
		
		if (ast is null)
			return;
		
		var collector = new Collector();
		collector.Collect(ast);
		
		var resolver = new Resolver(collector);
		var resolvedAst = resolver.Resolve(ast);
		;
	}
}