using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cella.Compiler.Linking;
using Cella.Compiler.Projects;
using Cella.Core.Analysis;
using Cella.Core.Binding;
using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.CodeGen;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Compiler;

internal static class Program
{
	public static async Task Main(string[] args)
	{
		if (args.Length == 0)
			return;
		
		// Phase 0a: Project collection
		var sourcePath = args[0];
		var projectPaths = new HashSet<string>();
		
		if (Directory.Exists(sourcePath))
			projectPaths.UnionWith(CellaProject.FindProjects(sourcePath));
		else if (File.Exists(sourcePath))
			projectPaths.Add(sourcePath);
		
		if (projectPaths.Count == 0)
		{
			Console.WriteLine("No projects provided, exiting");
			return;
		}
		
		// Phase 0b: Dependency graph
		var cts = new CancellationTokenSource();
		Dictionary<ProjectInfo, ImmutableHashSet<ProjectInfo>> projectDependencies;
		try
		{
			var dependencyGraph = await MapDependenciesAsync(projectPaths);
			projectDependencies = dependencyGraph.GetResolutionOrder().ToDictionary(static p => p,
				p => dependencyGraph.GetDependencies(p).ToImmutableHashSet());
		}
		catch (InvalidOperationException e)
		{
			// TEMP
			Console.WriteLine(e.Message);
			return;
		}
		
		var projectSymbols = new Dictionary<ProjectInfo, AssemblyInfo>();
		
		foreach (var (project, dependencies) in projectDependencies)
		{
			var dependencyInfo = new List<AssemblyInfo>();
			foreach (var dependency in dependencies)
				if (projectSymbols.TryGetValue(dependency, out var assemblySymbol))
					dependencyInfo.Add(assemblySymbol);
			
			projectSymbols[project] = await BuildProject(project, dependencyInfo, cts.Token);
		}
	}
	
	private static async Task<DependencyGraph<ProjectInfo>> MapDependenciesAsync(HashSet<string> projectPaths)
	{
		var projectLookup = new Dictionary<string, ProjectInfo>();
		var projectDependencies = new Dictionary<ProjectInfo, HashSet<string>>();
		var projectQueue = new Queue<string>(projectPaths.Select(Path.GetFullPath));
		var processedPaths = new HashSet<string>();
		var graph = new DependencyGraph<ProjectInfo>();
		
		// Collect projects
		while (projectQueue.Count > 0)
		{
			var path = projectQueue.Dequeue();
			if (!processedPaths.Add(path))
				continue;
			
			CellaProject project;
			await using (var stream = new FileStream(path, FileMode.Open))
				project = await CellaProject.LoadAsync(stream);
			
			var projectDirectory = Path.GetDirectoryName(path) ?? string.Empty;
			var projectName = Path.GetFileNameWithoutExtension(path);
			var projectInfo = new ProjectInfo(path, projectDirectory, projectName, project);
			graph.Add(projectInfo);
			projectLookup[path] = projectInfo;
			
			// Queue dependencies to be collected
			if (project.ProjectReferences is not { } references)
				continue;
			
			foreach (var reference in references)
			{
				var dependencyPath = Path.GetFullPath(Path.Combine(projectDirectory, reference.Path));
				
				projectDependencies.GetOrAdd(projectInfo).Add(dependencyPath);
				
				if (!processedPaths.Contains(dependencyPath))
					projectQueue.Enqueue(dependencyPath);
			}
		}
		
		// Map project dependencies
		foreach (var (project, dependencies) in projectDependencies)
			foreach (var dependency in dependencies)
				graph.AddDependency(project, projectLookup[dependency]);
		
		return graph;
	}
	
	private readonly record struct AssemblyInfo
	(
		AssemblySymbol AssemblySymbol,
		ProjectOutputType OutputType,
		string? OutputPath
	);
	
	private static async Task<AssemblyInfo> BuildProject(ProjectInfo project,
		IEnumerable<AssemblyInfo> dependencies, CancellationToken ct = default)
	{
		// Phase 1: File parsing
		var outputType = project.Project.OutputType;
		var files = await ProcessProject(project, ct);
		
		if (files.Length == 0)
			return new(new(project.Name, SymbolTable.Empty, SignatureTable.Empty, null), outputType, null);
		
		// Phase 2a: Symbol collection
		SymbolTable symbolTable;
		{
			var symbolCollector = new SymbolCollector();
			foreach (var info in files)
				symbolCollector.Collect(info.Ast);
			
			symbolTable = symbolCollector.Build();
		}
		
		var dependencyList = dependencies.ToImmutableArray();
		var dependencySymbols = dependencyList.Select(static d => d.AssemblySymbol).ToImmutableArray();
		
		// Phase 2b: Signature collection
		// TODO Allow configuring entry point name?
		var entryPointName = outputType == ProjectOutputType.Executable ? "main" : null;
		
		var typeMemberTable = new TypeMemberTable();
		typeMemberTable.CreateNativeMembers();
		
		var typePool = new TypePool(typeMemberTable);
		AssemblySymbol assemblySymbol;
		{
			var signatureCollector = new SignatureCollector(entryPointName, symbolTable, typePool, typeMemberTable,
				dependencySymbols);
			
			foreach (var info in files)
				signatureCollector.Collect(info.Ast);
			
			assemblySymbol = signatureCollector.FinishAssembly(project.Name);
		}
		
		var errorResult = new AssemblyInfo(assemblySymbol, outputType, null);
		
		if (outputType == ProjectOutputType.Executable && assemblySymbol.EntryPoint is null)
		{
			// TODO Diagnostics
			return errorResult;
		}
		
		// TODO Should I make a dependency here on LLVM? This implies a Language Server would also have to do this
		// We need to know the pointer size of the target for proper symbol resolution
		var targetTriple = TargetTriple.FromHost(); // TODO Check CLI args for cross-compilation
		
		var objDir = Path.Combine(project.Directory, "obj");
		var outputConfig = new OutputConfig(objDir, true, true);
		var targetConfig = new TargetConfig(targetTriple.ToLlvm());
		var codeGenConfig = new CodeGenConfig(outputConfig, targetConfig);
		var pointerBitSize = codeGenConfig.GetPointerSize() * 8;
		
		// Phase 3: Symbol resolution
		ImmutableArray<ResolvedSourceFileInfo> resolvedFiles;
		{
			var resolver = new Resolver(assemblySymbol, dependencySymbols, typePool, typeMemberTable, pointerBitSize);
			
			resolvedFiles = files
				.Select(sfi => new ResolvedSourceFileInfo(sfi.FilePath, resolver.Resolve(sfi.Ast), sfi.Source))
				.ToImmutableArray();
		}
		
		// Phase 4: Type checking
		{
			var typeChecker = new TypeChecker();
			
			foreach (var (_, resolvedAst, _) in resolvedFiles)
				typeChecker.Check(resolvedAst);
			
			if (typeChecker.Diagnostics.Count > 0)
			{
				foreach (var diagnostic in typeChecker.Diagnostics)
					Console.WriteLine(diagnostic);
				
				return errorResult;
			}
		}
		
		// Phase 5: Lowering
		var lowerer = new Lowerer();
		{
			foreach (var (_, resolvedAst, _) in resolvedFiles)
				lowerer.Lower(resolvedAst);
		}
		
		// Phase 6: Control flow analysis
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
				
				return errorResult;
			}
		}
		
		// Phase 7: Code generation
		string outputPath;
		{
			var codeGenerator = new CodeGenerator(assemblySymbol, typeMemberTable, codeGenConfig);
			
			var externalLibraries = new HashSet<string>();
			var objectFiles = new List<string>();
			foreach (var module in lowerer.Modules)
			{
				var result = codeGenerator.Generate(module);
				if (!result.IsSuccess)
				{
					Console.WriteLine($"Error: {result.ErrorMessage}");
					continue;
				}
				
				objectFiles.Add(result.OutputPath!);
				externalLibraries.UnionWith(result.ExternalLibraries);
			}
			
			var outputBaseName = project.Project.AssemblyName ?? project.Name;
			var outputFileName = GetOutputFileName(outputBaseName, targetTriple, outputType);
			var outputDir = Path.Combine(project.Directory, "bin");
			outputPath = Path.Combine(outputDir, outputFileName);
			
			if (!Directory.Exists(outputDir))
				Directory.CreateDirectory(outputDir);
			
			// TODO Need a more robust way of getting libs
			var libFiles = externalLibraries
				.Concat(dependencyList.Select(static d => d.OutputPath))
				.Select(static p => Path.ChangeExtension(p, ".lib"))
				.WhereNot(string.IsNullOrEmpty)
				.ToImmutableHashSet();
			
			// TODO Toolchains and linker paths should be grabbed from environment variables, compiler installation location
			const string toolchainDir = @"C:\cella\toolchains";
			var linker = new Linker(@"C:\cella\");
			var linkPreference = project.Project.SystemLinkPreference;
			var linkRequest = new LinkRequest(outputType, objectFiles, outputPath, toolchainDir, libFiles!)
			{
				LinkPreference = linkPreference
			};
			
			var linkerToolchain = Toolchain.FromTargetTriple(targetTriple, toolchainDir);
			var linkExitCode = await linker.LinkAsync(linkRequest, linkerToolchain);
			
			Console.WriteLine($"Linker finished with exit code {linkExitCode}");
			if (linkExitCode != 0)
				return errorResult;
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
		return new(assemblySymbol, outputType, outputPath);
	}
	
	private static async Task<ImmutableArray<SourceFileInfo>> ProcessProject(ProjectInfo project, CancellationToken ct = default)
	{
		var files = new ConcurrentBag<SourceFileInfo>();
		var filePaths = CellaProject.FindSourceFiles(project.Directory);
		
		foreach (var sourcePath in filePaths.AsParallel())
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
			
			var fileName = Path.GetRelativePath(project.Directory, sourcePath);
			
			var parser = new FileParser(tokens, fileName);
			var ast = parser.Parse();
			ct.ThrowIfCancellationRequested();
			
			// TODO Make opt-in via CLI flags
			Console.WriteLine($"\n====== {fileName} ======");
			Console.WriteLine(ast is null ? "Failed to parse." : AstPrinter.Print(ast));
			
			if (ast is not null)
				files.Add(new(sourcePath, ast, source));
		}
		
		return files.ToImmutableArray();
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
			_ => ".dll"
		},
		TargetTriple.OsTypes.Linux => outputType switch
		{
			ProjectOutputType.Executable => null,
			ProjectOutputType.StaticLibrary => ".a",
			_ => ".so"
		},
		TargetTriple.OsTypes.MacOsX => outputType switch
		{
			ProjectOutputType.Executable => null,
			ProjectOutputType.StaticLibrary => ".a",
			_ => ".dylib"
		},
		_ => null
	};
}

internal readonly record struct ProjectInfo
(
	string FilePath,
	string Directory,
	string Name,
	CellaProject Project
);

internal readonly record struct SourceFileInfo(string FilePath, FileNode Ast, ISource Source);
internal readonly record struct ResolvedSourceFileInfo(string FilePath, ResolvedFileNode Ast, ISource Source);