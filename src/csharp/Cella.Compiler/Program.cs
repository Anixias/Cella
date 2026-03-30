using System.Collections.Immutable;
using System.Diagnostics;
using Cella.Compiler.Linking;
using Cella.Core.Analysis;
using Cella.Core.Binding;
using Cella.Core.CodeGen;
using Cella.Core.Lowering;
using Cella.Core.Syntax;
using Cella.Core.Text;

namespace Cella.Compiler;

internal static class Program
{
	public static async Task Main(string[] args)
	{
		if (args.Length == 0)
			return;
		
		// TEMP
		var sourceFile = args[0];
		if (!File.Exists(sourceFile))
			return;
		
		// TODO Create some kind of StreamSource so the entire file doesn't have to be loaded into memory
		var sourceString = await File.ReadAllTextAsync(sourceFile);
		var source = new StringSource(sourceString, sourceFile);
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
		
		if (typeChecker.Diagnostics.Count > 0)
		{
			foreach (var diagnostic in typeChecker.Diagnostics)
				Console.WriteLine(diagnostic);
			
			return;
		}
		
		var lowerer = new Lowerer();
		lowerer.Lower(resolvedAst);
		
		var controlFlowAnalyzer = new ControlFlowAnalyzer();
		
		foreach (var module in lowerer.Modules)
		{
			Console.WriteLine(LoweredModulePrinter.Print(module));
			
			foreach (var function in module.Functions)
				controlFlowAnalyzer.Analyze(function);
		}
		
		if (controlFlowAnalyzer.Diagnostics.Count > 0)
		{
			foreach (var diagnostic in controlFlowAnalyzer.Diagnostics)
				Console.WriteLine(diagnostic);
			
			return;
		}
		
		var objDir = "TODO";
		var outputConfig = new OutputConfig(objDir, true, true);
		var codeGenConfig = new CodeGenConfig(outputConfig, null);
		var codeGenerator = new CodeGenerator(codeGenConfig);
		
		var objectFiles = new List<string>();
		foreach (var module in lowerer.Modules)
			if (codeGenerator.Generate(module) is { } objectFile)
				objectFiles.Add(objectFile);
		
		var outputPath = "TODO";
		var targetTriple = codeGenerator.TargetTriple;
		
		// TODO Toolchains and linker paths should be grabbed from environment variables, compiler installation location
		var linker = new Linker(@"C:\cella\lld.exe");
		var linkRequest = new LinkRequest(objectFiles, outputPath);
		var linkerToolchain = Toolchain.FromTargetTriple(targetTriple, @"C:\cella\toolchains");
		var linkExitCode = await linker.LinkAsync(linkRequest, linkerToolchain);
		
		Console.WriteLine($"Linker finished with exit code {linkExitCode}");
		if (linkExitCode != 0)
			return;
		
		var userProgram = new Process
		{
			StartInfo =
			{
				FileName = outputPath
			}
		};
		
		userProgram.Start();
		await userProgram.WaitForExitAsync();
		
		Console.WriteLine($"User program finished with exit code {userProgram.ExitCode}");
	}
}