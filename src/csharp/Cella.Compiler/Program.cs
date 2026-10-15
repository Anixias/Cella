using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Cella.Compiler.Linking;
using Cella.Compiler.Projects;
using Cella.Core.Analysis;
using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.CodeGen;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Compiler;

internal static class Program
{
	private const int CompilerStackSize = 256 * 1024 * 1024;
	
	public static Task<int> Main(string[] args)
	{
		var result = new TaskCompletionSource<int>();
		var thread = new Thread(() =>
		{
			var context = new CompilerSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(context);
			var run = RunAsync(args);
			run.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
			context.Run();
			if (run.IsFaulted)
				result.SetException(run.Exception!.InnerExceptions);
			else if (run.IsCanceled)
				result.SetCanceled();
			else
				result.SetResult(run.Result);
		}, CompilerStackSize);
		
		thread.Start();
		return result.Task;
	}
	
	private static readonly string[] _options = ["--quiet", "--hints", "--release", "--flag"];
	
	private static async Task<int> RunAsync(string[] args)
	{
		if (args.Where((arg, i) =>
				    arg.StartsWith("--") && !_options.Contains(arg) && (i == 0 || args[i - 1] != "--flag"))
			    .FirstOrDefault() is { } unknown)
		{
			Console.WriteLine($"Unknown option '{unknown}'");
			return 1;
		}
		
		var verbose = !args.Contains("--quiet");
		var showHints = args.Contains("--hints");
		var optimizeMode = args.Contains("--release") ? OptimizeMode.Release : OptimizeMode.Debug;
		if (ParseFlagArguments(args) is not { } flagOverrides)
			return 1;
		
		var paths = args.Where((arg, i) => !arg.StartsWith("--") && (i == 0 || args[i - 1] != "--flag")).ToArray();
		if (paths is [])
		{
			Console.WriteLine(
				"Usage: cella <project file or folder> [--release] [--quiet] [--hints] [--flag <name>[=true|false]]");
			
			return 1;
		}
		
		if (paths is [_, var extra, ..])
		{
			Console.WriteLine($"Unexpected argument '{extra}'");
			return 1;
		}
		
		var sourcePath = paths[0];
		
		if (!Directory.Exists(sourcePath) && !File.Exists(sourcePath))
		{
			Console.WriteLine($"'{sourcePath}' not found");
			return 1;
		}
		
		// Phase 0a: Project collection
		var projectPaths = new HashSet<string>();
		
		if (Directory.Exists(sourcePath))
			projectPaths.UnionWith(CellaProject.FindProjects(sourcePath));
		else if (File.Exists(sourcePath) && !CellaProject.IsProjectFile(sourcePath))
		{
			Console.WriteLine($"'{sourcePath}' is not a project file");
			return 1;
		}
		else if (File.Exists(sourcePath))
			projectPaths.Add(sourcePath);
		
		if (projectPaths.Count == 0)
		{
			Console.WriteLine($"No projects found in '{sourcePath}'");
			return 1;
		}
		
		// Phase 0b: Dependency graph
		var targetTriple = TargetTriple.FromHost(); // TODO Check CLI args for cross-compilation
		var projectErrors = new List<string>();
		var dependencyGraph = await MapDependenciesAsync(projectPaths, targetTriple, optimizeMode, flagOverrides,
			projectErrors);
		
		if (dependencyGraph.FindCycle() is [_, ..] cycle)
		{
			foreach (var error in projectErrors)
				Console.WriteLine(error);
			
			var names = cycle.Append(cycle[0]).Select(static project => Path.GetFileName(project.FilePath));
			Console.WriteLine($"Project references form a cycle: {string.Join(" -> ", names)}");
			return 1;
		}
		
		var projectDependencies = dependencyGraph.GetResolutionOrder().ToDictionary(static p => p,
			p => dependencyGraph.GetDependencies(p).ToImmutableArray());
		
		var builtInFlags = BuildFlags.Create(targetTriple, optimizeMode);
		var flagsValid = ValidateFlags(projectDependencies.Keys, builtInFlags, flagOverrides) &
		                 ValidateOutputNames(projectDependencies);
		
		foreach (var error in projectErrors)
			Console.WriteLine(error);
		
		if (!flagsValid || projectErrors.Count > 0)
			return 1;
		
		var conversionTable = ConversionTable.CreateNative();
		var operatorRegistry = new OperatorRegistry();
		var sizeTable = new SizeTable();
		var typePool = new TypePool(conversionTable, operatorRegistry, sizeTable);
		var projectSymbols = new Dictionary<ProjectInfo, AssemblyInfo>();
		var succeeded = true;
		var displayRoot = Directory.Exists(sourcePath)
			? Path.GetFullPath(sourcePath)
			: Path.GetDirectoryName(Path.GetFullPath(sourcePath));
		
		foreach (var (project, dependencies) in projectDependencies)
		{
			if (!dependencies.All(projectSymbols.ContainsKey))
			{
				succeeded = false;
				continue;
			}
			
			var dependencyInfo = dependencies.Select(dependency => projectSymbols[dependency]).ToList();
			var assemblyInfo = await BuildProject(project, typePool, dependencyInfo, verbose, showHints, optimizeMode,
				targetTriple, flagOverrides, displayRoot);
			
			if (assemblyInfo.Succeeded)
				projectSymbols[project] = assemblyInfo;
			else
				succeeded = false;
		}
		
		return succeeded ? 0 : 1;
	}
	
	private static string GetOutputBaseName(ProjectInfo project) => project.Project.AssemblyName ?? project.Name;
	
	private static bool ValidateOutputNames(Dictionary<ProjectInfo, ImmutableArray<ProjectInfo>> dependencies)
	{
		var reported = new HashSet<string>();
		foreach (var project in dependencies.Keys)
		{
			var closure = new HashSet<ProjectInfo>();
			var pending = new Stack<ProjectInfo>([project]);
			while (pending.TryPop(out var next))
			{
				if (closure.Add(next))
					foreach (var dependency in dependencies.GetValueOrDefault(next, []))
						pending.Push(dependency);
			}
			
			var clashes = closure
				.GroupBy(GetOutputBaseName, StringComparer.OrdinalIgnoreCase)
				.Where(static sameName => sameName.Count() > 1)
				.OrderBy(static sameName => sameName.Key, StringComparer.OrdinalIgnoreCase);
			
			foreach (var sameName in clashes)
			{
				var paths = sameName.Select(static clash => $"'{clash.FilePath}'").Order(StringComparer.Ordinal)
					.ToList();
				
				var described = $"{string.Join(", ", paths[..^1])} and {paths[^1]}";
				if (reported.Add(described))
					Console.WriteLine(
						$"Projects {described} {(paths.Count == 2 ? "both" : "all")} build '{sameName.Key}'");
			}
		}
		
		return reported.Count == 0;
	}
	
	private static Dictionary<string, bool>? ParseFlagArguments(string[] args)
	{
		var flags = new Dictionary<string, bool>();
		for (var i = 0; i < args.Length; i++)
		{
			if (args[i] != "--flag")
				continue;
			
			if (++i == args.Length || args[i].StartsWith("--"))
			{
				Console.WriteLine("Expected a flag after '--flag'");
				return null;
			}
			
			var parts = args[i].Split('=', 2);
			if (parts[0].Length == 0)
			{
				Console.WriteLine("Expected a flag after '--flag'");
				return null;
			}
			
			if (parts is [_, not ("true" or "false")])
			{
				Console.WriteLine($"Expected 'true' or 'false' for flag '{parts[0]}'");
				return null;
			}
			
			flags[parts[0]] = parts is [_] or [_, "true"];
		}
		
		return flags;
	}
	
	private static Dictionary<string, bool> CreateFlags(CellaProject project, TargetTriple targetTriple,
		OptimizeMode optimizeMode, IReadOnlyDictionary<string, bool> flagOverrides)
	{
		var flags = BuildFlags.Create(targetTriple, optimizeMode);
		foreach (var (name, value) in project.Flags ?? [])
			flags[name] = flagOverrides.GetValueOrDefault(name, value);
		
		return flags;
	}
	
	private static bool ValidateFlags(IEnumerable<ProjectInfo> projects, IReadOnlyDictionary<string, bool> builtIn,
		IReadOnlyDictionary<string, bool> overrides)
	{
		var declared = new HashSet<string>();
		var isValid = true;
		foreach (var project in projects)
		{
			foreach (var name in project.Project.Flags?.Keys ?? Enumerable.Empty<string>())
			{
				var problem = builtIn.ContainsKey(name) ? $"Flag '{name}' is built in"
					: BuildFlags.IsValidName(name) ? null
					: $"'{name}' is not a valid flag name";
				
				if (problem is null)
				{
					declared.Add(name);
					continue;
				}
				
				Console.WriteLine($"{project.FilePath}: {problem}");
				isValid = false;
			}
		}
		
		foreach (var name in overrides.Keys.Where(name => builtIn.ContainsKey(name) || !declared.Contains(name)))
		{
			Console.WriteLine(builtIn.ContainsKey(name)
				? $"Cannot set built-in flag '{name}'"
				: $"Flag '{name}' not found");
			
			isValid = false;
		}
		
		return isValid;
	}
	
	private static string GetCanonicalPath(string path)
	{
		var full = Path.GetFullPath(path);
		if (!OperatingSystem.IsWindows() || Path.GetPathRoot(full) is not { Length: > 0 } root)
			return full;
		
		var canonical = root.ToUpperInvariant();
		foreach (var part in full[root.Length..]
			         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
		{
			var match = Directory.Exists(canonical)
				? Directory.EnumerateFileSystemEntries(canonical, part).FirstOrDefault()
				: null;
			
			canonical = match ?? Path.Combine(canonical, part);
		}
		
		return canonical;
	}
	
	private static async Task<DependencyGraph<ProjectInfo>> MapDependenciesAsync(HashSet<string> projectPaths,
		TargetTriple targetTriple, OptimizeMode optimizeMode, IReadOnlyDictionary<string, bool> flagOverrides,
		List<string> errors)
	{
		var projectLookup = new Dictionary<string, ProjectInfo>();
		var projectDependencies = new Dictionary<ProjectInfo, HashSet<string>>();
		var projectQueue = new Queue<string>(projectPaths.Select(GetCanonicalPath));
		var processedPaths = new HashSet<string>();
		var graph = new DependencyGraph<ProjectInfo>();
		
		// Collect projects
		while (projectQueue.Count > 0)
		{
			var path = projectQueue.Dequeue();
			if (!processedPaths.Add(path))
				continue;
			
			CellaProject? loaded;
			await using (var stream = new FileStream(path, FileMode.Open))
				loaded = await CellaProject.LoadAsync(stream, path, errors);
			
			if (loaded is null)
				continue;
			
			if (CellaProject.FindSiblingProjects(path).Select(static name => $"'{name}'").ToList() is [_, ..] siblings)
				errors.Add($"{path}: Cannot share a directory with {string.Join(", ", siblings)}");
			
			var project = loaded.Resolve(path, CreateFlags(loaded, targetTriple, optimizeMode, flagOverrides), errors);
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
				var dependencyPath = GetCanonicalPath(Path.Combine(projectDirectory, reference.Path!));
				if (!File.Exists(dependencyPath))
				{
					errors.Add($"{path}: Project '{reference.Path}' not found");
					continue;
				}
				
				if (!CellaProject.IsProjectFile(dependencyPath))
				{
					errors.Add($"{path}: '{reference.Path}' is not a project file");
					continue;
				}
				
				projectDependencies.GetOrAdd(projectInfo).Add(dependencyPath);
				
				if (!processedPaths.Contains(dependencyPath))
					projectQueue.Enqueue(dependencyPath);
			}
		}
		
		// Map project dependencies
		foreach (var (project, dependencies) in projectDependencies)
		{
			foreach (var dependency in dependencies)
			{
				if (projectLookup.TryGetValue(dependency, out var target))
					graph.AddDependency(project, target);
			}
		}
		
		return graph;
	}
	
	private readonly record struct AssemblyInfo
	(
		AssemblySymbol AssemblySymbol,
		ProjectOutputType OutputType,
		string? OutputPath,
		bool Succeeded
	)
	{
		public ImmutableArray<AssemblyInfo> Dependencies { get; init; } = [];
		public ImmutableArray<LoweredFunction> GenericFunctions { get; init; } = [];
		public IReadOnlySet<GlobalSymbol> ExportedStatics { get; init; } = ImmutableHashSet<GlobalSymbol>.Empty;
		public IReadOnlySet<Symbol> Exports { get; init; } = ImmutableHashSet<Symbol>.Empty;
		public ImmutableArray<ExportName> ExportNames { get; init; } = [];
		public ImmutableArray<string> PlainNames { get; init; } = [];
		public ImmutableArray<string> ContainedLibraries { get; init; } = [];
		public ImmutableArray<string> LibraryFiles { get; init; } = [];
		public string? Directory { get; init; }
		public string Name => AssemblySymbol.Name;
	}
	
	private static Dictionary<string, List<AssemblyInfo>> FindProviders(ImmutableArray<AssemblyInfo> dependencies) =>
		dependencies
			.Where(static dependency => dependency.OutputType != ProjectOutputType.StaticLibrary)
			.SelectMany(static dependency => dependency.ContainedLibraries
				.Select(library => (Library: library, Provider: dependency)))
			.GroupBy(static pair => pair.Library)
			.ToDictionary(static group => group.Key,
				static group => group.Select(static pair => pair.Provider).DistinctBy(static p => p.Name).ToList());
	
	private static string DescribeProjects(IReadOnlyList<string> names) => names.Count == 2
		? $"both '{names[0]}' and '{names[1]}'"
		: $"{string.Join(", ", names.SkipLast(1).Select(static name => $"'{name}'"))} and '{names[^1]}'";
	
	private static ImmutableArray<string> GetPlainNames(AssemblySymbol assembly) =>
	[
		..assembly.SymbolTable.DeclarationSymbols.Values
			.Select(static symbol => symbol switch
			{
				FunctionSymbol { Syntax: FunctionNode } function => function.ExternalName,
				GlobalSymbol { IsImport: false } global => global.ExternalName,
				_ => null
			})
			.OfType<string>()
			.Distinct()
	];
	
	private static ImmutableArray<AssemblyInfo> CollectDependencies(ImmutableArray<AssemblyInfo> dependencies)
	{
		var collected = new List<AssemblyInfo>();
		var seen = new HashSet<AssemblySymbol>();
		Collect(dependencies);
		return [..collected];
		
		void Collect(ImmutableArray<AssemblyInfo> items)
		{
			foreach (var item in items)
			{
				if (!seen.Add(item.AssemblySymbol))
					continue;
				
				collected.Add(item);
				Collect(item.Dependencies);
			}
		}
	}
	
	private static IEnumerable<Symbol> GetLibrarySymbols(AssemblyInfo library) =>
		library.AssemblySymbol.SignatureTable.Functions.Keys
			.Concat<Symbol>(library.AssemblySymbol.SignatureTable.Globals.Keys)
			.Concat(library.ExportedStatics);
	
	private static async Task<AssemblyInfo> BuildProject(ProjectInfo project, TypePool typePool,
		IEnumerable<AssemblyInfo> dependencies, bool verbose, bool showHints, OptimizeMode optimizeMode,
		TargetTriple targetTriple, IReadOnlyDictionary<string, bool> flagOverrides, string? displayRoot,
		CancellationToken ct = default)
	{
		// Phase 1: File parsing
		var outputType = project.Project.OutputType;
		var flags = CreateFlags(project.Project, targetTriple, optimizeMode, flagOverrides);
		var (files, parseDiagnostics) = await ProcessProject(project, flags, ct);
		var diagnostics = new List<DiagnosticList> { parseDiagnostics };
		
		if (ReportErrors(diagnostics, displayRoot, showHints))
			return new(new(project.Name, SymbolTable.Empty, SignatureTable.Empty, null), outputType, null, false);
		
		if (files.Length == 0)
		{
			if (outputType != ProjectOutputType.Executable)
				return new(new(project.Name, SymbolTable.Empty, SignatureTable.Empty, null), outputType, null, true);
			
			PrintDiagnostics([new(DiagnosticSeverity.Error, SourceLocation.None, "The program has no 'main' function")],
				displayRoot, showHints);
			
			return new(new(project.Name, SymbolTable.Empty, SignatureTable.Empty, null), outputType, null, false);
		}
		
		// Phase 2a: Symbol collection
		SymbolTable symbolTable;
		{
			var symbolCollector = new SymbolCollector();
			foreach (var ast in files)
				symbolCollector.Collect(ast);
			
			symbolTable = symbolCollector.Build();
		}
		
		var dependencyList = dependencies.ToImmutableArray();
		var dependencySymbols = dependencyList.Select(static d => d.AssemblySymbol).ToImmutableArray();
		var allDependencies = CollectDependencies(dependencyList);
		var providers = FindProviders(allDependencies);
		if (outputType != ProjectOutputType.StaticLibrary)
		{
			var duplicated = providers
				.Where(static pair => pair.Value.Count > 1)
				.Select(static pair =>
					$"'{pair.Key}' is linked into {DescribeProjects([..pair.Value.Select(static p => p.Name)])}")
				.ToList();
			
			if (duplicated.Count > 0)
			{
				foreach (var problem in duplicated)
					Console.WriteLine($"{project.FilePath}: {problem}");
				
				return new(new(project.Name, SymbolTable.Empty, SignatureTable.Empty, null), outputType, null, false);
			}
		}
		
		var contained = outputType == ProjectOutputType.StaticLibrary
			? []
			: allDependencies
				.Where(dependency => dependency.OutputType == ProjectOutputType.StaticLibrary &&
				                     !providers.ContainsKey(dependency.Name))
				.ToList();
		
		// TODO Should I make a dependency here on LLVM? This implies a Language Server would also have to do this
		// We need to know the pointer size of the target for proper symbol resolution
		var objDir = Path.Combine(project.Directory, "obj");
		var outputConfig = new OutputConfig(objDir, true, true);
		var targetConfig = new TargetConfig(targetTriple.ToLlvm(), Features: targetTriple.ToLlvmFeatures());
		var codeGenConfig = new CodeGenConfig(outputConfig, targetConfig, optimizeMode)
		{
			BoundsChecks = project.Project.BoundsChecks ?? true,
			OverflowChecks = project.Project.OverflowChecks ?? optimizeMode == OptimizeMode.Debug,
			DebugInfo = project.Project.DebugInfo ?? true,
			SourceRoot = project.Directory,
			ProjectRoots = [..allDependencies.Select(static d => d.Directory).OfType<string>(), project.Directory],
			ExportsSymbols = outputType != ProjectOutputType.StaticLibrary
		};
		
		var pointerBitSize = codeGenConfig.GetPointerSize() * 8;
		
		// Phase 2b: Signature collection
		// TODO Allow configuring entry point name?
		var entryPointName = outputType == ProjectOutputType.Executable ? "main" : null;
		var indirectSymbols = allDependencies
			.Select(static d => d.AssemblySymbol)
			.Except(dependencySymbols)
			.ToImmutableArray();
		
		var signatureCollector = new SignatureCollector(project.Name, entryPointName, symbolTable, typePool,
			dependencySymbols, indirectSymbols, pointerBitSize);
		
		var resolver = new Resolver(symbolTable, signatureCollector, typePool, pointerBitSize);
		signatureCollector.Collect([..files], resolver);
		
		var assemblySymbol = signatureCollector.FinishAssembly(project.Name);
		var errorResult = new AssemblyInfo(assemblySymbol, outputType, null, false);
		
		diagnostics.Add(signatureCollector.Diagnostics);
		diagnostics.Add(resolver.Diagnostics);
		if (ReportErrors(diagnostics, displayRoot, showHints))
			return errorResult;
		
		// Phase 3: Symbol resolution
		ImmutableArray<ResolvedFileNode> resolvedFiles;
		{
			resolvedFiles = [..files.Select(resolver.Resolve)];
			
			resolver.ReportInfiniteInstantiations();
			if (ReportErrors(diagnostics, displayRoot, showHints))
				return errorResult;
		}
		
		// Phase 4: Type checking
		{
			var typeChecker = new TypeChecker(signatureCollector.Evaluator, typePool, signatureCollector.Modules);
			diagnostics.Add(typeChecker.Diagnostics);
			
			foreach (var resolvedAst in resolvedFiles)
				typeChecker.Check(resolvedAst);
			
			if (ReportErrors(diagnostics, displayRoot, showHints))
				return errorResult;
		}
		
		// Phase 5: Lowering
		var lowerer = new Lowerer(signatureCollector.Evaluator, typePool,
			g => signatureCollector.GetGlobalInfo(g)!.Value);
		
		{
			foreach (var resolvedAst in resolvedFiles)
				lowerer.Lower(resolvedAst);
		}
		
		// Phase 6: Control flow analysis
		{
			var cfgDiagnostics = new DiagnosticList();
			diagnostics.Add(cfgDiagnostics);
			var controlFlowAnalyzer = new ControlFlowAnalyzer(cfgDiagnostics);
			var memoryChecker = new MemoryChecker(typePool, signatureCollector.Evaluator, cfgDiagnostics);
			
			foreach (var module in lowerer.Modules)
			{
				var checkable = new List<LoweredFunction>();
				foreach (var file in module.Files)
				{
					foreach (var function in file.Functions)
					{
						controlFlowAnalyzer.Analyze(function);
						function.Normalize();
						checkable.Add(function);
					}
				}
				
				foreach (var function in checkable.OrderBy(static function => function.Info.Symbol.Captures.IsEmpty))
					memoryChecker.Check(function);
				
				if (verbose)
					Console.WriteLine(LoweredModulePrinter.Print(module));
			}
			
			if (ReportErrors(diagnostics, displayRoot, showHints))
				return errorResult;
			
			PrintDiagnostics(diagnostics.SelectMany(static list => list), displayRoot, showHints);
		}
		
		// Phase 7: Code generation
		string outputPath;
		ImmutableArray<string> ownLibraries;
		var genericFunctions = lowerer.Modules
			.SelectMany(static module => module.Files)
			.SelectMany(static file => file.Functions)
			.Where(static function => !function.Info.Symbol.TypeParameters.IsEmpty)
			.ToImmutableArray();
		
		IReadOnlySet<GlobalSymbol> exportedStatics;
		var exports = new ExportSurface(assemblySymbol, typePool, genericFunctions).Symbols;
		ImmutableArray<ExportName> exportNames;
		var plainNames = GetPlainNames(assemblySymbol);
		{
			var inputs = new CodeGenInputs
			{
				GenericFunctions = [..genericFunctions, ..allDependencies.SelectMany(static d => d.GenericFunctions)],
				Exports = exports,
				LibraryStatics = allDependencies.SelectMany(static d => d.ExportedStatics).ToHashSet(),
				StaticLibrarySymbols = allDependencies
					.Where(d => d.OutputType == ProjectOutputType.StaticLibrary && !providers.ContainsKey(d.Name))
					.SelectMany(GetLibrarySymbols)
					.ToHashSet(),
				DynamicAssemblies = allDependencies
					.Where(static d => d.OutputType != ProjectOutputType.StaticLibrary)
					.Select(static d => d.Name)
					.Concat(providers.Keys)
					.ToHashSet(),
				HostedRegistries = contained
					.Where(static library => library.Exports.Any(static symbol => symbol is GlobalSymbol
					{
						ContainingType: NamedTypeSymbol { IsGenericDefinition: true }
					}))
					.Select(static library => library.Name)
					.ToHashSet()
			};
			
			using var codeGenerator = new CodeGenerator(assemblySymbol, typePool, codeGenConfig,
				signatureCollector.Modules, inputs);
			
			var externalLibraries = new HashSet<string>();
			var objectFiles = new List<string>();
			var codeGenFailed = false;
			foreach (var result in codeGenerator.Generate(lowerer.Modules))
			{
				if (!result.IsSuccess)
				{
					Console.WriteLine($"Error: {result.ErrorMessage}");
					codeGenFailed = true;
					continue;
				}
				
				objectFiles.Add(result.OutputPath!);
				externalLibraries.UnionWith(result.ExternalLibraries);
			}
			
			if (ReportErrors([codeGenerator.Diagnostics], displayRoot, showHints))
				return errorResult;
			
			if (codeGenFailed)
				return errorResult;
			
			exportedStatics = codeGenerator.ExportedStatics;
			exportNames = [..codeGenerator.ExportNames];
			var outputBaseName = GetOutputBaseName(project);
			var outputFileName = GetOutputFileName(outputBaseName, targetTriple, outputType);
			var outputDir = Path.Combine(project.Directory, "bin");
			outputPath = Path.Combine(outputDir, outputFileName);
			
			if (!Directory.Exists(outputDir))
				Directory.CreateDirectory(outputDir);
			
			var conflicts = contained
				.SelectMany(static library => library.PlainNames.Select(name => (Name: name, Owner: library.Name)))
				.Concat(plainNames.Select(name => (Name: name, Owner: project.Name)))
				.GroupBy(static pair => pair.Name)
				.Where(static sameName => sameName.Count() > 1)
				.Select(static sameName =>
					$"'{sameName.Key}' is defined by {DescribeProjects([..sameName.Select(static pair => pair.Owner)])}")
				.ToList();
			
			if (conflicts.Count > 0)
			{
				foreach (var problem in conflicts)
					Console.WriteLine($"{project.FilePath}: {problem}");
				
				return errorResult;
			}
			
			string? moduleDefinition = null;
			var reexports = contained.SelectMany(static library => library.ExportNames).Distinct().ToList();
			if (reexports.Count > 0)
			{
				moduleDefinition = Path.Combine(objDir, $"{project.Name}.def");
				await File.WriteAllLinesAsync(moduleDefinition,
					["EXPORTS", ..reexports.Select(static name => $"\t\"{name.Name}\"{(name.IsData ? " DATA" : "")}")],
					ct);
			}
			
			ownLibraries =
			[
				..externalLibraries
					.Select(static name => Path.GetExtension(name).ToLowerInvariant() is ".lib" or ".dll"
						? Path.ChangeExtension(name, ".lib")
						: $"{name}.lib")
					.Distinct()
			];
			
			var libFiles = outputType == ProjectOutputType.StaticLibrary
				? []
				: ownLibraries
					.Concat(contained.SelectMany(static library => library.LibraryFiles))
					.Concat(allDependencies
						.Where(d => d.OutputType != ProjectOutputType.StaticLibrary || !providers.ContainsKey(d.Name))
						.Select(static d => Path.ChangeExtension(d.OutputPath, ".lib")))
					.WhereNot(string.IsNullOrEmpty)
					.Distinct()
					.ToList();
			
			var linker = new Linker(AppContext.BaseDirectory, verbose);
			var linkPreference = project.Project.SystemLinkPreference;
			var linkRequest = new LinkRequest(outputType, objectFiles, outputPath, libFiles!)
			{
				LinkPreference = linkPreference,
				DebugInfo = codeGenConfig.DebugInfo,
				OptimizeMode = optimizeMode,
				ModuleDefinition = moduleDefinition
			};
			
			if (linker.FindMissingLinker(linkRequest) is { } missing)
			{
				Console.WriteLine($"{project.FilePath}: Cannot find the linker '{missing}'");
				return errorResult;
			}
			
			var linkerToolchain = Toolchain.FromTargetTriple(targetTriple,
				Path.Combine(AppContext.BaseDirectory, "toolchains"));
			
			var linkExitCode = await linker.LinkAsync(linkRequest, linkerToolchain);
			
			if (verbose || linkExitCode != 0)
				Console.WriteLine($"Linker finished with exit code {linkExitCode}");
			
			if (linkExitCode != 0)
				return errorResult;
			
			if (outputType == ProjectOutputType.Executable)
				CopySharedLibraries(allDependencies, outputDir);
		}
		
		return new(assemblySymbol, outputType, outputPath, true)
		{
			Directory = project.Directory,
			Dependencies = dependencyList,
			GenericFunctions = genericFunctions,
			ExportedStatics = exportedStatics,
			Exports = exports,
			ExportNames = exportNames,
			PlainNames = plainNames,
			ContainedLibraries = [..contained.Select(static library => library.Name)],
			LibraryFiles = ownLibraries
		};
	}
	
	private static void CopySharedLibraries(ImmutableArray<AssemblyInfo> dependencies, string outputDir)
	{
		var libraries = dependencies.Where(static d => d.OutputType != ProjectOutputType.StaticLibrary);
		foreach (var path in libraries.Select(static d => d.OutputPath).OfType<string>())
		{
			File.Copy(path, Path.Combine(outputDir, Path.GetFileName(path)), true);
			var symbols = Path.ChangeExtension(path, ".pdb");
			if (File.Exists(symbols))
				File.Copy(symbols, Path.Combine(outputDir, Path.GetFileName(symbols)), true);
		}
	}
	
	private static async Task<(ImmutableArray<FileNode> Files, DiagnosticList Diagnostics)> ProcessProject(
		ProjectInfo project, IReadOnlyDictionary<string, bool> flags, CancellationToken ct = default)
	{
		var files = new List<FileNode>();
		var diagnostics = new DiagnosticList();
		foreach (var path in CellaProject.FindSourceFiles(project.Directory).Order(StringComparer.Ordinal))
		{
			var (ast, fileDiagnostics) = await ParseFile(project, flags, path, ct);
			diagnostics.AddRange(fileDiagnostics);
			if (ast is { IsExcluded: false })
				files.Add(ast);
		}
		
		return ([..files], diagnostics);
	}
	
	private static async Task<(FileNode? Ast, List<Diagnostic> Diagnostics)> ParseFile(ProjectInfo project,
		IReadOnlyDictionary<string, bool> flags, string sourcePath, CancellationToken ct)
	{
		// TODO Create some kind of StreamSource so the entire file doesn't have to be loaded into memory
		var bytes = await File.ReadAllBytesAsync(sourcePath, ct);
		ct.ThrowIfCancellationRequested();
		
		using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
		var source = new StringSource(await reader.ReadToEndAsync(ct), sourcePath);
		var diagnostics = new List<Diagnostic>();
		if (FindInvalidUtf8(bytes) is { } offset)
			diagnostics.Add(new(DiagnosticSeverity.Error, new SourceLocation(source, new TextRange(offset, offset + 1)),
				"Invalid UTF-8"));
		
		// TODO Change parser to retrieve tokens as needed
		var tokens = new FilteredScanner(source).ToImmutableArray();
		ct.ThrowIfCancellationRequested();
		
		var parser = new FileParser(tokens, Path.GetRelativePath(project.Directory, sourcePath), sourcePath, flags);
		var ast = parser.Parse();
		diagnostics.AddRange(parser.Diagnostics);
		return (ast, diagnostics);
	}
	
	private static int? FindInvalidUtf8(byte[] bytes)
	{
		if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
			return null;
		
		var start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
		for (var index = start; index < bytes.Length;)
		{
			if (Rune.DecodeFromUtf8(bytes.AsSpan(index), out _, out var length) != OperationStatus.Done)
				return Encoding.UTF8.GetCharCount(bytes, start, index - start);
			
			index += length;
		}
		
		return null;
	}
	
	private static bool ReportErrors(List<DiagnosticList> diagnostics, string? directory, bool showHints)
	{
		if (diagnostics.All(static list => list.ErrorCount == 0))
			return false;
		
		PrintDiagnostics(diagnostics.SelectMany(static list => list), directory, showHints);
		return true;
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
	
	private const int TabWidth = 4;
	
	private static void PrintDiagnostics(IEnumerable<Diagnostic> diagnostics, string? rootDirectory, bool showHints)
	{
		var diagnosticsByFile = diagnostics
			.Where(diagnostic => showHints || diagnostic.Severity != DiagnosticSeverity.Hint)
			.GroupBy(static d => d.SourceLocation.Source.FilePath)
			.ToDictionary(static g => g.Key, static g => g
				.OrderBy(static d => d.Line)
				.ThenBy(static d => d.Column)
				.ToArray());
		
		foreach (var (file, set) in diagnosticsByFile.OrderBy(static kvp => kvp.Key))
		{
			if (!string.IsNullOrEmpty(file))
			{
				var path = rootDirectory is not null ? Path.GetRelativePath(rootDirectory, file) : file;
				
				WriteColored($"\n====== {path} ======{Environment.NewLine}{Environment.NewLine}", ConsoleColor.Blue);
			}
			
			foreach (var diagnostic in set)
				PrintDiagnostic(diagnostic);
		}
	}
	
	private static void PrintDiagnostic(Diagnostic diagnostic)
	{
		var severity = diagnostic.Severity;
		var (source, range) = diagnostic.SourceLocation;
		
		if (!range.IsValid)
		{
			var end = source.GetText(new(0, source.Length)).TrimEnd().Length;
			range = new(end, end);
		}
		
		var (severityColor, severityLabel) = severity switch
		{
			DiagnosticSeverity.Error => (ConsoleColor.Red, "Error"),
			DiagnosticSeverity.Warning => (ConsoleColor.Yellow, "Warning"),
			DiagnosticSeverity.Hint => (ConsoleColor.Green, "Hint"),
			_ => (ConsoleColor.Gray, "Info")
		};
		
		if (diagnostic.SourceLocation == SourceLocation.None)
		{
			WriteColored(severityLabel, severityColor);
			Console.Write(": ");
			WriteColored(diagnostic.Message, severityColor);
			Console.WriteLine();
			Console.WriteLine();
			
			return;
		}
		
		const ConsoleColor suggestionColor = ConsoleColor.Cyan;
		
		var (startLine, startCol) = source.GetLineColumn(range.Start);
		var (endLine, endCol) = source.GetLineColumn(range.End);
		
		var firstDisplayLine = Math.Max(1, startLine - 1);
		var gutterWidth = endLine.ToString().Length;
		
		var lineNumbers = Enumerable.Range(firstDisplayLine, endLine - firstDisplayLine + 1).ToList();
		var rawLines = lineNumbers.Select(n => source.GetText(source.GetLineRange(n)).ToString()).ToList();
		
		var commonLeading = rawLines
			.Where(static l => l.Any(static c => !char.IsWhiteSpace(c)))
			.Select(LeadingVisualWidth)
			.DefaultIfEmpty(0)
			.Min();
		
		var strippedLines = rawLines
			.Select(l => StripLeading(l, commonLeading))
			.Select(static r => r.OvershootSpaces > 0 ? new string(' ', r.OvershootSpaces) + r.Text : r.Text)
			.ToList();
		
		WriteColored($"{severityLabel}", severityColor);
		
		if (lineNumbers.Count == 0)
		{
			Console.Write($" at line {startLine}, column {startCol}: ");
			WriteColored(diagnostic.Message, severityColor);
			Console.WriteLine();
			Console.WriteLine();
			
			return;
		}
		
		Console.WriteLine($" at line {startLine}, column {startCol}");
		for (var i = 0; i < lineNumbers.Count; i++)
		{
			var lineNum = lineNumbers[i];
			var stripped = strippedLines[i];
			var isPreceding = lineNum < startLine;
			
			WriteColored(
				$"{lineNum.ToString().PadLeft(gutterWidth)} │ ",
				isPreceding ? ConsoleColor.DarkGray : severityColor);
			
			if (isPreceding)
			{
				WriteColored(ExpandTabs(stripped), ConsoleColor.DarkGray);
				Console.WriteLine();
				continue;
			}
			
			var rawLine = rawLines[i];
			var hlVisStart = lineNum == startLine
				? Math.Max(0, VisualColumn(rawLine, startCol - 1) - commonLeading)
				: LeadingVisualWidth(stripped);
			
			var hlVisEnd = lineNum == endLine
				? Math.Max(0, VisualColumn(rawLine, endCol - 1) - commonLeading)
				: VisualColumn(stripped, stripped.Length);
			
			var lineVisLen = VisualColumn(stripped, stripped.Length);
			hlVisStart = Math.Clamp(hlVisStart, 0, lineVisLen);
			hlVisEnd = Math.Clamp(hlVisEnd, 0, lineVisLen);
			
			var visualCol = 0;
			foreach (var ch in stripped)
			{
				var expanded = ch == '\t' ? new string(' ', TabWidth - visualCol % TabWidth) : ch.ToString();
				var charVisWidth = expanded.Length;
				var inHighlight = visualCol >= hlVisStart && visualCol < hlVisEnd;
				
				if (inHighlight)
					WriteColored(expanded, severityColor);
				else
					Console.Write(expanded);
				
				visualCol += charVisWidth;
			}
			
			Console.WriteLine();
			
			var arrowCount = Math.Max(0, hlVisEnd - hlVisStart);
			var isLast = lineNum == endLine;
			if (!isLast && arrowCount == 0)
				continue;
			
			var gutter = new string(' ', gutterWidth);
			WriteColored($"{gutter}   ", ConsoleColor.DarkGray);
			Console.Write(new string(' ', hlVisStart));
			
			var arrowLine = arrowCount switch
			{
				> 2 => '╘' + new string('═', arrowCount - 2) + '╛',
				2 => "╘╛",
				_ => "^"
			};
			
			WriteColored(arrowLine, severityColor);
			if (!isLast)
			{
				Console.WriteLine();
				continue;
			}
			
			WriteColored($" {diagnostic.Message}", severityColor);
			Console.WriteLine();
			
			var pipeColumn = hlVisStart + Math.Max(arrowCount, 1) - 1;
			foreach (var hint in diagnostic.Hints)
			{
				WriteColored($"{gutter}   {new string(' ', pipeColumn)}| ", ConsoleColor.DarkGray);
				WriteColored(hint, suggestionColor);
				Console.WriteLine();
			}
			
			Console.WriteLine();
		}
	}
	
	private static int LeadingVisualWidth(string line)
	{
		var col = 0;
		foreach (var ch in line)
		{
			if (ch == ' ')
				col++;
			else if (ch == '\t')
				col += TabWidth - col % TabWidth;
			else
				break;
		}
		
		return col;
	}
	
	private static (string Text, int OvershootSpaces) StripLeading(string line, int targetVisualColumns)
	{
		var col = 0;
		var i = 0;
		while (i < line.Length && col < targetVisualColumns)
		{
			if (line[i] == ' ')
			{
				col++;
				i++;
			}
			else if (line[i] == '\t')
			{
				var tabStop = TabWidth - col % TabWidth;
				if (col + tabStop > targetVisualColumns)
				{
					i++;
					var overshoot = col + tabStop - targetVisualColumns;
					return (line[i..], overshoot);
				}
				
				col += tabStop;
				i++;
			}
			else
				break;
		}
		
		return (line[i..], 0);
	}
	
	private static int VisualColumn(string text, int charIndex)
	{
		var col = 0;
		for (var i = 0; i < charIndex && i < text.Length; i++)
			col += text[i] == '\t' ? TabWidth - col % TabWidth : 1;
		
		return col;
	}
	
	private static string ExpandTabs(string text)
	{
		var sb = new StringBuilder(text.Length);
		var col = 0;
		foreach (var c in text)
		{
			if (c == '\t')
			{
				var spaces = TabWidth - col % TabWidth;
				sb.Append(' ', spaces);
				col += spaces;
			}
			else
			{
				sb.Append(c);
				col++;
			}
		}
		
		return sb.ToString();
	}
	
	private static void WriteColored(string text, ConsoleColor color)
	{
		if (Console.IsOutputRedirected)
		{
			Console.Write(text);
			return;
		}
		
		var prev = Console.ForegroundColor;
		Console.ForegroundColor = color;
		Console.Write(text);
		Console.ForegroundColor = prev;
	}
}

internal readonly record struct ProjectInfo
(
	string FilePath,
	string Directory,
	string Name,
	CellaProject Project
);