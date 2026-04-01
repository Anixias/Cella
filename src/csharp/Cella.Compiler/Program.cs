using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cella.Compiler.Linking;
using Cella.Compiler.Projects;
using Cella.Core.Analysis;
using Cella.Core.Binding;
using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.CodeGen;
using Cella.Core.Lowering;
using Cella.Core.Syntax;
using Cella.Core.Syntax.Nodes.Declarations;
using Cella.Core.Text;

namespace Cella.Compiler;

internal static class Program
{
	public static async Task Main(string[] args)
	{
		if (args.Length == 0)
			return;
		
		// Phase 0: Project collection
		var sourcePath = args[0];
		var projectPaths = new List<string>();
		
		if (Directory.Exists(sourcePath))
			projectPaths.AddRange(CellaProject.FindProjects(sourcePath));
		else if (File.Exists(sourcePath))
			projectPaths.Add(sourcePath);
		
		if (projectPaths.Count == 0)
		{
			Console.WriteLine("No projects provided, exiting");
			return;
		}
		
		await Parallel.ForEachAsync(projectPaths, async (p, ct) => await BuildProject(p, ct));
	}
	
	private static async Task BuildProject(string projectPath, CancellationToken ct = default)
	{
		// Phase 1: File parsing
		var (_, projectDirectory, projectName, project, files) = await ProcessProject(projectPath, ct);
		
		if (files.Length == 0)
			return;
		
		var collectorContext = new CollectorContext();
		
		// Phase 2: Type collection
		{
			var typeCollector = new TypeCollector(collectorContext);
			
			foreach (var (_, ast, _) in files)
				typeCollector.Collect(ast);
		}
		
		// Phase 3: Declaration collection
		{
			var declarationCollector = new DeclarationCollector(collectorContext);
			
			foreach (var (_, ast, _) in files)
				declarationCollector.Collect(ast);
		}
		
		// Phase 4: Symbol resolution
		ImmutableArray<ResolvedSourceFileInfo> resolvedFiles;
		{
			var resolver = new Resolver(collectorContext);
			
			resolvedFiles = files
				.Select(sfi => new ResolvedSourceFileInfo(sfi.FilePath, resolver.Resolve(sfi.Ast), sfi.Source))
				.ToImmutableArray();
		}
		
		// Phase 5: Type checking
		{
			var typeChecker = new TypeChecker();
			
			foreach (var (_, resolvedAst, _) in resolvedFiles)
				typeChecker.Check(resolvedAst);
			
			if (typeChecker.Diagnostics.Count > 0)
			{
				foreach (var diagnostic in typeChecker.Diagnostics)
					Console.WriteLine(diagnostic);
				
				return;
			}
		}
		
		// Phase 6: Lowering
		var lowerer = new Lowerer();
		{
			foreach (var (_, resolvedAst, _) in resolvedFiles)
				lowerer.Lower(resolvedAst);
		}
		
		// Phase 7: Control flow analysis
		{
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
		}
		
		// Phase 8: Code generation
		{
			var targetTriple = TargetTriple.FromHost(); // TODO Check CLI args for cross-compilation
			
			var objDir = Path.Combine(projectDirectory, "obj");
			var outputConfig = new OutputConfig(objDir, true, true);
			var targetConfig = new TargetConfig(targetTriple.ToLlvm());
			var codeGenConfig = new CodeGenConfig(outputConfig, targetConfig);
			var codeGenerator = new CodeGenerator(codeGenConfig);
			
			var objectFiles = new List<string>();
			foreach (var module in lowerer.Modules)
				if (codeGenerator.Generate(module) is { } objectFile)
					objectFiles.Add(objectFile);
			
			var outputBaseName = project.AssemblyName ?? projectName;
			var outputFileName = GetOutputFileName(outputBaseName, targetTriple, project.OutputType);
			var outputPath = Path.Combine(projectDirectory, "bin", outputFileName);
			
			// TODO Toolchains and linker paths should be grabbed from environment variables, compiler installation location
			var linker = new Linker(@"C:\cella\");
			var linkRequest = new LinkRequest(project.OutputType, objectFiles, outputPath);
			var linkerToolchain = Toolchain.FromTargetTriple(targetTriple, @"C:\cella\toolchains");
			var linkExitCode = await linker.LinkAsync(linkRequest, linkerToolchain);
			
			Console.WriteLine($"Linker finished with exit code {linkExitCode}");
			if (linkExitCode != 0)
				return;
		}
		
		/*var userProgram = new Process
		{
			StartInfo =
			{
				FileName = outputPath
			}
		};
		
		userProgram.Start();
		await userProgram.WaitForExitAsync();
		
		Console.WriteLine($"User program finished with exit code {userProgram.ExitCode}");*/
	}
	
	private static async Task<ProjectInfo> ProcessProject(string filePath, CancellationToken ct = default)
	{
		CellaProject project;
		await using (var stream = new FileStream(filePath, FileMode.Open))
		{
			project = await CellaProject.LoadAsync(stream);
		}
		
		var projectName = Path.GetFileNameWithoutExtension(filePath);
		var projectDirectory = Path.GetDirectoryName(filePath)!;
		var fileInfos = new ConcurrentBag<SourceFileInfo>();
		
		var sourceFiles = CellaProject.FindSourceFiles(projectDirectory);
		foreach (var sourcePath in sourceFiles.AsParallel())
		{
			ct.ThrowIfCancellationRequested();
			
			// TODO Create some kind of StreamSource so the entire file doesn't have to be loaded into memory
			var sourceString = await File.ReadAllTextAsync(sourcePath, ct);
			ct.ThrowIfCancellationRequested();
			
			var source = new StringSource(sourceString, sourcePath);
			var scanner = new FilteredScanner(source);
			
			// TEMP
			/*foreach (var token in scanner)
				Console.WriteLine(token);*/
			
			// TODO Change parser to retrieve tokens as needed
			var tokens = scanner.ToImmutableArray();
			ct.ThrowIfCancellationRequested();
			
			var parser = new FileParser(tokens);
			var ast = parser.Parse();
			ct.ThrowIfCancellationRequested();
			
			// TODO Make opt-in via CLI flags
			Console.WriteLine($"\n====== {Path.GetRelativePath(projectDirectory, sourcePath)} ======");
			Console.WriteLine(ast is null ? "Failed to parse." : AstPrinter.Print(ast));
			
			if (ast is not null)
				fileInfos.Add(new(sourcePath, ast, source));
		}
		
		return new(filePath, projectDirectory, projectName, project, fileInfos.ToImmutableArray());
	}
	
	public static string GetOutputFileName(string baseName, TargetTriple target, ProjectOutputType outputType)
	{
		var ext = GetOutputExtension(target, outputType);
		
		var needsLibPrefix =
			outputType != ProjectOutputType.Executable &&
			target.Os is TargetTriple.OsTypes.Linux or TargetTriple.OsTypes.MacOsX;
		
		return needsLibPrefix
			? $"lib{baseName}{ext}"
			: $"{baseName}{ext}";
	}
	
	private static string? GetOutputExtension(TargetTriple target, ProjectOutputType outputType) => target.Os switch
	{
		TargetTriple.OsTypes.Windows => outputType switch
		{
			ProjectOutputType.Executable => ".exe",
			ProjectOutputType.StaticLibrary => ".lib",
			_ => ".dll",
		},
		TargetTriple.OsTypes.Linux => outputType switch
		{
			ProjectOutputType.Executable => null,
			ProjectOutputType.StaticLibrary => ".a",
			_ => ".so",
		},
		TargetTriple.OsTypes.MacOsX => outputType switch
		{
			ProjectOutputType.Executable => null,
			ProjectOutputType.StaticLibrary => ".a",
			_ => ".dylib",
		},
		_ => null
	};
}

internal readonly record struct ProjectInfo
(
	string FilePath,
	string Directory,
	string Name,
	CellaProject Project,
	ImmutableArray<SourceFileInfo> Files
);
internal readonly record struct SourceFileInfo(string FilePath, FileNode Ast, ISource Source);
internal readonly record struct ResolvedSourceFileInfo(string FilePath, ResolvedFileNode Ast, ISource Source);