using System.Collections.Immutable;
using Cella.Compiler.Linking;
using Cella.Compiler.Projects;
using Cella.Core.Analysis;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding;
using Cella.Core.CodeGen;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Compiler.Building;

public sealed partial class Builder
{
	private static Lazy<SemanticModel> CreateModel(ImmutableArray<ResolvedFileNode> files,
		List<(Token Name, Symbol Symbol)> names, TypePool typePool)
	{
		var model = new SemanticModel(files, names, typePool.NeedsDrop);
		return new(() => model);
	}
	
	private async Task<AssemblyInfo> BuildProject(ProjectInfo project, ImmutableArray<ProjectInfo> references,
		TypePool typePool, IEnumerable<AssemblyInfo> dependencies, SystemLinkPreference linkPreference,
		CancellationToken ct)
	{
		var targetTriple = options.Target;
		var optimizeMode = options.OptimizeMode;
		
		// Phase 1: File parsing
		var isEntry = options.Entry is not null && project.FilePath == entryProjectPath;
		var outputType = isEntry ? ProjectOutputType.Executable : project.Project.OutputType;
		var isReferenceable = !isEntry && project.Project.Referenceable == true && outputType.IsExecutable();
		var entryPointName = isEntry ? options.Entry : outputType.IsExecutable() ? "main" : null;
		var flags = CreateFlags(project.Project);
		var (files, parseDiagnostics) = await ProcessProject(project, references, flags, ct);
		var diagnostics = new List<DiagnosticList> { parseDiagnostics };
		var phases = new Dictionary<Diagnostic, int>(ReferenceEqualityComparer.Instance);
		RecordPhase(diagnostics, phases, 0);
		
		if (files.Length == 0)
		{
			if (ReportErrors(diagnostics))
				return new(new(GetIdentity(project), SymbolTable.Empty, SignatureTable.Empty, null), outputType, null,
					false);
			
			if (!outputType.IsExecutable())
				return new(new(GetIdentity(project), SymbolTable.Empty, SignatureTable.Empty, null), outputType, null,
					true);
			
			output.Report([
				new(DiagnosticSeverity.Error, SourceLocation.None,
					$"The program has no '{entryPointName}' function")
			]);
			
			return new(new(GetIdentity(project), SymbolTable.Empty, SignatureTable.Empty, null), outputType, null,
				false);
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
		var dropOrder = OrderForDrops(dependencyList);
		var providers = FindProviders(allDependencies);
		if (outputType != ProjectOutputType.StaticLibrary)
		{
			var duplicated = providers
				.Where(static pair => pair.Value.Count > 1)
				.Select(static pair =>
					$"'{pair.Key}' is linked into {DescribeProjects([..pair.Value.Select(static p => p.Name)])}")
				.ToList();
			
			foreach (var problem in duplicated.Select(problem => ProjectDiagnostic.In(project.FilePath, problem)))
			{
				_problems.Add(problem);
				output.WriteLine(problem.Text);
				generatesCode = false;
			}
		}
		
		var contained = outputType == ProjectOutputType.StaticLibrary
			? []
			: allDependencies
				.Where(dependency => dependency.OutputType == ProjectOutputType.StaticLibrary &&
				                     !providers.ContainsKey(dependency.Name))
				.ToList();
		
		var objDir = Path.Combine(isEntry ? GetEntryDirectory(project) : GetOutputDirectory(project), "obj");
		var outputConfig = new OutputConfig(objDir, options.EmitsIr, options.EmitsAssembly);
		var targetConfig = new TargetConfig(targetTriple.ToLlvm(), targetTriple.ToSystem(),
			targetTriple.ToArchitecture(),
			Features: targetTriple.ToLlvmFeatures());
		
		var codeGenConfig = new CodeGenConfig(outputConfig, targetConfig, optimizeMode)
		{
			Checks = new(BuildFlags.ChecksBounds(project.Project),
				BuildFlags.ChecksOverflow(project.Project, optimizeMode)),
			DebugInfo = project.Project.DebugInfo ?? true,
			ExportsSymbols = outputType != ProjectOutputType.StaticLibrary,
			StartFunction = isReferenceable ? Mangling.MangleStart(GetIdentity(project)) : null
		};
		
		var pointerBitSize = targetTriple.PointerBitSize;
		
		// Phase 2b: Signature collection
		// TODO Allow configuring entry point name?
		var indirectSymbols = allDependencies
			.Select(static d => d.AssemblySymbol)
			.Except(dependencySymbols)
			.ToImmutableArray();
		
		var names = new List<(Token Name, Symbol Symbol)>();
		var recordName = options.ChecksOnly
			? (name, symbol) => names.Add((name, symbol))
			: (Action<Token, Symbol>?)null;
		
		var signatureCollector = new SignatureCollector(GetIdentity(project), entryPointName, symbolTable, typePool,
			dependencySymbols, indirectSymbols, FindAliases(project, dependencyList), pointerBitSize)
		{
			QualifiesEntryPoint = isEntry,
			RecordName = recordName,
			Probe = probe
		};
		
		foreach (var alias in signatureCollector.Modules.AliasConflicts)
		{
			var problem = ProjectDiagnostic.In(project.FilePath, $"'{alias}' names a project reference and a module");
			_problems.Add(problem);
			output.WriteLine(problem.Text);
			generatesCode = false;
		}
		
		var containment = new ErrorContainment(files);
		containment.MarkSyntaxErrors(parseDiagnostics);
		var resolver = new Resolver(symbolTable, signatureCollector, typePool, pointerBitSize)
		{
			Containment = containment,
			RecordName = recordName,
			Probe = probe
		};
		
		signatureCollector.Collect([..files], resolver);
		
		var assemblySymbol = signatureCollector.FinishAssembly(GetIdentity(project));
		var plainNames = GetPlainNames(assemblySymbol);
		ImmutableArray<string> containedNames = [..contained.Select(static library => library.Name)];
		var errorResult = new AssemblyInfo(assemblySymbol, outputType, null, false)
		{
			PlainNames = plainNames,
			ContainedLibraries = containedNames
		};
		
		var plainNameConflicts = contained
			.SelectMany(static library => library.PlainNames.Select(name => (Name: name, Owner: library.Name)))
			.Concat(plainNames.Select(name => (Name: name, Owner: project.Name)))
			.GroupBy(static pair => pair.Name)
			.Where(static sameName => sameName.Count() > 1)
			.Select(static sameName =>
				$"'{sameName.Key}' is defined by {DescribeProjects([..sameName.Select(static pair => pair.Owner)])}");
		
		foreach (var problem in plainNameConflicts.Select(problem => ProjectDiagnostic.In(project.FilePath, problem)))
		{
			_problems.Add(problem);
			output.WriteLine(problem.Text);
			generatesCode = false;
		}
		
		diagnostics.Add(signatureCollector.Diagnostics);
		diagnostics.Add(resolver.Diagnostics);
		RecordPhase(diagnostics, phases, 1);
		
		// Phase 3: Symbol resolution
		ImmutableArray<ResolvedFileNode> resolvedFiles;
		IReadOnlyDictionary<string, FunctionInfo> panicAdapters;
		{
			(resolvedFiles, panicAdapters) = resolver.AddPanicAdapters([..files.Select(resolver.Resolve)]);
			
			resolver.ReportInfiniteInstantiations();
			resolver.ReportAtomicInstances();
			resolver.ReportTraitConstantInstances();
			RecordPhase(diagnostics, phases, 2);
		}
		
		containment.AddDeclarations(resolvedFiles);
		containment.MarkErrors(diagnostics.SelectMany(static list => list));
		
		// Phase 4: Type checking
		{
			var typeChecker = new TypeChecker(signatureCollector.Evaluator, typePool, signatureCollector.Modules,
				containment);
			
			diagnostics.Add(typeChecker.Diagnostics);
			foreach (var resolvedAst in resolvedFiles)
				typeChecker.Check(resolvedAst);
			
			typeChecker.Complete();
			containment.MarkErrors(typeChecker.Diagnostics);
			RecordPhase(diagnostics, phases, 3);
		}
		
		// Phase 5: Lowering
		var lowerer = new Lowerer(signatureCollector.Evaluator, typePool,
			g => signatureCollector.GetGlobalInfo(g)!.Value, containment);
		
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
				
				foreach (var file in module.Files)
					file.Functions.RemoveAll(static function => function.IsCheckOnly);
				
				if (options.Verbose)
					output.WriteLine(LoweredModulePrinter.Print(module));
			}
			
			var found = diagnostics.SelectMany(static list => list).ToList();
			diagnostics = [new(containment.Filter(found, diagnostic => phases.GetValueOrDefault(diagnostic, 4)))];
			
			_projects.Add(new(project.FilePath, [..diagnostics.SelectMany(static list => list)], resolvedFiles)
			{
				Model = options.ChecksOnly ? CreateModel(resolvedFiles, names, typePool) : null
			});
			
			if (ReportErrors(diagnostics))
				return errorResult;
			
			output.Report(diagnostics.SelectMany(static list => list));
		}
		
		if (options.ChecksOnly || !generatesCode)
			return new(assemblySymbol, outputType, null, true)
			{
				Directory = project.Directory,
				ProjectPath = project.FilePath,
				Dependencies = dependencyList,
				PlainNames = plainNames,
				ContainedLibraries = containedNames
			};
		
		// Phase 7: Code generation
		string outputPath;
		string? debugSymbols;
		ImmutableArray<string> ownLibraries;
		ImmutableArray<string> ownImports;
		ImmutableArray<string> ownGlobalImports;
		var genericFunctions = lowerer.Modules
			.SelectMany(static module => module.Files)
			.SelectMany(static file => file.Functions)
			.Where(static function => !function.Info.Symbol.TypeParameters.IsEmpty)
			.ToImmutableArray();
		
		IReadOnlySet<GlobalSymbol> exportedStatics;
		bool hasStaticDrops;
		var exports = new ExportSurface(assemblySymbol, typePool, genericFunctions).Symbols;
		ImmutableArray<ExportName> exportNames;
		{
			var inputs = new CodeGenInputs
			{
				GenericFunctions = [..genericFunctions, ..allDependencies.SelectMany(static d => d.GenericFunctions)],
				ForeignChecks = allDependencies
					.SelectMany(static d => d.GenericFunctions.Select(f => (f.Info.Symbol, d.Checks)))
					.ToDictionary(static pair => pair.Symbol, static pair => pair.Checks),
				AssemblyChecks = allDependencies.ToDictionary(static d => d.Name, static d => d.Checks),
				Exports = exports,
				PanicAdapters = panicAdapters,
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
					.ToHashSet(),
				ContainedAssemblies =
				[
					..dropOrder.Where(d => contained.Contains(d) && d.HasStaticDrops)
						.Select(static d => d.Name)
				],
				LoadedAssemblies =
				[
					..dropOrder.Where(static d => d.OutputType != ProjectOutputType.StaticLibrary && d.HasStaticDrops)
						.Select(static d => d.Name)
				]
			};
			
			using var codeGenerator = new CodeGenerator(assemblySymbol, typePool, codeGenConfig,
				signatureCollector.Modules, inputs);
			
			var externalLibraries = new HashSet<string>();
			var importedFunctions = new HashSet<string>();
			var importedGlobals = new HashSet<string>();
			var objectFiles = new List<string>();
			var codeGenFailed = false;
			foreach (var result in codeGenerator.Generate(lowerer.Modules))
			{
				if (!result.IsSuccess)
				{
					output.WriteLine($"Error: {result.ErrorMessage}");
					codeGenFailed = true;
					continue;
				}
				
				objectFiles.Add(result.OutputPath!);
				externalLibraries.UnionWith(result.ExternalLibraries);
				importedFunctions.UnionWith(result.ImportedFunctions);
				importedGlobals.UnionWith(result.ImportedGlobals);
			}
			
			if (ReportErrors([codeGenerator.Diagnostics]))
				return errorResult;
			
			if (codeGenFailed)
				return errorResult;
			
			exportedStatics = codeGenerator.ExportedStatics;
			hasStaticDrops = codeGenerator.HasStaticDrops;
			exportNames = [..codeGenerator.ExportNames];
			var outputBaseName = GetOutputBaseName(project);
			var outputFileName = GetOutputFileName(outputBaseName, targetTriple, outputType);
			var outputDir = isEntry ? GetEntryDirectory(project) : Path.Combine(GetOutputDirectory(project), "bin");
			outputPath = Path.Combine(outputDir, outputFileName);
			if (project.FilePath == programProjectPath)
				programPath = outputPath;
			
			if (!Directory.Exists(outputDir))
				Directory.CreateDirectory(outputDir);
			
			string? moduleDefinition = null;
			var reexports = contained.SelectMany(static library => library.ExportNames).Distinct().ToList();
			if (reexports.Count > 0 && !targetTriple.IsElf)
			{
				moduleDefinition = Path.Combine(objDir, $"{project.Name}.def");
				await File.WriteAllLinesAsync(moduleDefinition,
					["EXPORTS", ..reexports.Select(static name => $"\t\"{name.Name}\"{(name.IsData ? " DATA" : "")}")],
					ct);
			}
			
			ownImports = [..importedFunctions];
			ownGlobalImports = [..importedGlobals];
			ownLibraries =
			[
				..externalLibraries.Select(origin => ResolveLibrary(origin, project.Directory, targetTriple)).Distinct()
			];
			
			var libFiles = outputType == ProjectOutputType.StaticLibrary
				? []
				: ownLibraries
					.Concat(contained.SelectMany(static library => library.LibraryFiles))
					.Concat(allDependencies
						.Where(d => d.OutputType != ProjectOutputType.StaticLibrary || !providers.ContainsKey(d.Name))
						.Select(d => targetTriple.IsElf ? d.OutputPath : Path.ChangeExtension(d.OutputPath, ".lib")))
					.WhereNot(string.IsNullOrEmpty)
					.Distinct()
					.ToList();
			
			var linkedType = isReferenceable ? ProjectOutputType.SharedLibrary : outputType;
			var linkedPath = isReferenceable
				? Path.Combine(outputDir, GetOutputFileName(outputBaseName, targetTriple, linkedType))
				: outputPath;
			
			var linker = new Linker(Installation.ToolsDirectory, options.Verbose ? output.WriteLine : null);
			var linkRequest = new LinkRequest(linkedType, objectFiles, linkedPath, libFiles!)
			{
				LinkPreference = linkPreference,
				DebugInfo = codeGenConfig.DebugInfo,
				Natvis = codeGenConfig.DebugInfo && targetTriple.Os == TargetTriple.OsTypes.Windows
					? WriteNatvis(objDir)
					: null,
				OptimizeMode = optimizeMode,
				ModuleDefinition = moduleDefinition,
				KeptSymbols = targetTriple.IsElf ? [..reexports.Select(static name => name.Name)] : [],
				WideEntry = codeGenerator.UsesWideEntry,
				ForcedSymbols =
				[
					..importedFunctions.Concat(contained.SelectMany(static library => library.ImportedFunctions))
						.Concat(FindStaticGlobals(
							importedGlobals.Concat(contained.SelectMany(static library => library.ImportedGlobals)),
							libFiles,
							targetTriple))
						.Distinct()
				]
			};
			
			var linkerToolchain = Toolchain.FromTargetTriple(targetTriple);
			
			if (linker.FindMissingLinker(linkRequest, linkerToolchain) is { } missing)
			{
				output.WriteLine($"{project.FilePath}: Cannot find the linker '{missing}'");
				return errorResult;
			}
			
			var (linkExitCode, linkErrors) = await linker.LinkAsync(linkRequest, linkerToolchain);
			debugSymbols = linkedPath == outputPath ? linkerToolchain.GetDebugSymbolsPath(linkRequest) : null;
			if (linkExitCode != 0 && DescribeLinkErrors(linkErrors, [
				    (assemblySymbol, project.Directory), ..contained
					    .Select(static library => (library.AssemblySymbol, library.Directory!))
			    ], targetTriple, linkerToolchain) is { } described)
			{
				ReportErrors([described]);
				return errorResult;
			}
			
			if (linkExitCode != 0)
				output.WriteError(linkErrors);
			
			if (options.Verbose || linkExitCode != 0)
				output.WriteLine($"Linker finished with exit code {linkExitCode}");
			
			if (linkExitCode != 0)
				return errorResult;
			
			if (isReferenceable)
			{
				var launcher = codeGenerator.GenerateLauncher(codeGenConfig.StartFunction!);
				if (!launcher.IsSuccess)
				{
					output.WriteLine($"Error: {launcher.ErrorMessage}");
					return errorResult;
				}
				
				var launcherRequest = new LinkRequest(outputType, [launcher.OutputPath!], outputPath,
					targetTriple.IsElf ? linkedPath : Path.ChangeExtension(linkedPath, ".lib"))
				{
					LinkPreference = linkPreference,
					OptimizeMode = optimizeMode,
					WideEntry = codeGenerator.UsesWideEntry
				};
				
				var (launcherExitCode, launcherErrors) = await linker.LinkAsync(launcherRequest, linkerToolchain);
				if (launcherExitCode != 0)
				{
					output.WriteError(launcherErrors);
					output.WriteLine($"Linker finished with exit code {launcherExitCode}");
					return errorResult;
				}
				
				outputPath = linkedPath;
			}
			
			if (outputType.IsExecutable())
				CopySharedLibraries(allDependencies, outputDir);
		}
		
		if (!_outputDirectories.ContainsKey(project.FilePath))
			output.ReportOutput(project.Name, outputPath);
		
		return new(assemblySymbol, isReferenceable ? ProjectOutputType.SharedLibrary : outputType, outputPath, true)
		{
			Directory = project.Directory,
			ProjectPath = project.FilePath,
			Dependencies = dependencyList,
			GenericFunctions = genericFunctions,
			Checks = codeGenConfig.Checks,
			ExportedStatics = exportedStatics,
			HasStaticDrops = hasStaticDrops,
			Exports = exports,
			ExportNames = exportNames,
			PlainNames = plainNames,
			ContainedLibraries = containedNames,
			DebugSymbols = debugSymbols,
			LibraryFiles = ownLibraries,
			ImportedFunctions = ownImports,
			ImportedGlobals = ownGlobalImports
		};
	}
	
	private static void RecordPhase(List<DiagnosticList> diagnostics, Dictionary<Diagnostic, int> phases, int phase)
	{
		foreach (var diagnostic in diagnostics.SelectMany(static list => list))
			phases.TryAdd(diagnostic, phase);
	}
	
	private bool ReportErrors(List<DiagnosticList> diagnostics)
	{
		if (diagnostics.All(static list => list.ErrorCount == 0))
			return false;
		
		output.Report(diagnostics.SelectMany(static list => list));
		return true;
	}
}