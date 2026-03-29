using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Lowering;
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
		
		var collectorContext = new CollectorContext();
		
		// TODO Why do functions have resolved return types already?? That should be in resolver pass...
		// Should there be an unresolved function symbol and a resolved function symbol??
		var typeCollector = new TypeCollector(collectorContext);
		typeCollector.Collect(ast);
		
		var declarationCollector = new DeclarationCollector(collectorContext);
		declarationCollector.Collect(ast);
		
		var resolver = new Resolver(collectorContext);
		var resolvedAst = resolver.Resolve(ast);
		
		var typeChecker = new TypeChecker();
		typeChecker.Check(resolvedAst);
		
		var lowerer = new Lowerer();
		lowerer.Lower(resolvedAst);
		
		foreach (var module in lowerer.Modules)
			Console.WriteLine(LoweredModulePrinter.Print(module));
		
		;
	}
}