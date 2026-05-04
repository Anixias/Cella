using System.Collections.Concurrent;
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
		
		var conversionTable = ConversionTable.CreateNative();
		var operatorRegistry = new OperatorRegistry(conversionTable);
		var sizeTable = new SizeTable();
		var typePool = new TypePool(conversionTable, operatorRegistry, sizeTable);
		var projectSymbols = new Dictionary<ProjectInfo, AssemblyInfo>();
		
		foreach (var (project, dependencies) in projectDependencies)
		{
			var dependencyInfo = new List<AssemblyInfo>();
			foreach (var dependency in dependencies)
				if (projectSymbols.TryGetValue(dependency, out var assemblySymbol))
					dependencyInfo.Add(assemblySymbol);
			
			projectSymbols[project] = await BuildProject(project, typePool, dependencyInfo, cts.Token);
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
	
	private static async Task<AssemblyInfo> BuildProject(ProjectInfo project, TypePool typePool,
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
		
		AssemblySymbol assemblySymbol;
		{
			var signatureCollector = new SignatureCollector(entryPointName, symbolTable, typePool, dependencySymbols);
			
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
			var resolver = new Resolver(assemblySymbol, dependencySymbols, typePool, pointerBitSize);
			
			resolvedFiles = files
				.Select(sfi => new ResolvedSourceFileInfo(sfi.FilePath, resolver.Resolve(sfi.Ast), sfi.Source))
				.ToImmutableArray();
			
			if (resolver.Diagnostics.ErrorCount > 0)
			{
				foreach (var error in resolver.Diagnostics.Errors)
					PrintDiagnostic(error);
				
				return errorResult;
			}
		}
		
		// Phase 4: Type checking
		{
			var typeChecker = new TypeChecker();
			
			foreach (var (_, resolvedAst, _) in resolvedFiles)
				typeChecker.Check(resolvedAst);
			
			if (typeChecker.Diagnostics.ErrorCount > 0)
			{
				foreach (var error in typeChecker.Diagnostics.Errors)
					PrintDiagnostic(error);
				
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
			var codeGenerator = new CodeGenerator(assemblySymbol, typePool, codeGenConfig);
			
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
	
	private static async Task<ImmutableArray<SourceFileInfo>> ProcessProject(ProjectInfo project,
		CancellationToken ct = default)
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
			
			if (ast is null)
			{
				Console.WriteLine($"\n====== {fileName} ======");
				foreach (var error in parser.Diagnostics.Errors)
					PrintDiagnostic(error);
			}
			else
			{
				// TODO Make opt-in via CLI flags
				Console.WriteLine(AstPrinter.Print(ast));
				files.Add(new(sourcePath, ast, source));
			}
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
	
	private const int TabWidth = 4;
	
	private static void PrintDiagnostic(Diagnostic diagnostic)
	{
		var severity = diagnostic.Severity;
		var (source, range) = diagnostic.SourceLocation;
		
		if (!range.IsValid)
		{
			var sourceLength = source.Length;
			var pos = sourceLength > 0 ? sourceLength - 1 : 0;
			range = new(pos, pos + 1);
		}
		
		var (severityColor, severityLabel) = severity switch
		{
			DiagnosticSeverity.Error => (ConsoleColor.Red, "Error"),
			DiagnosticSeverity.Warning => (ConsoleColor.Yellow, "Warning"),
			DiagnosticSeverity.Hint => (ConsoleColor.Green, "Hint"),
			_ => (ConsoleColor.Gray, "Info")
		};
		
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
				: 0;
			
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
			
			WriteColored($"{new string(' ', gutterWidth)}   ", ConsoleColor.DarkGray);
			Console.Write(new string(' ', hlVisStart));
			
			var arrowCount = Math.Max(0, hlVisEnd - hlVisStart);
			var arrowLine = arrowCount switch
			{
				> 2 => '╘' + new string('═', arrowCount - 2) + "╛ ",
				2 => "╘╛ ",
				_ => "^ "
			};
			
			WriteColored(arrowLine, severityColor);
			WriteColored(diagnostic.Message, severityColor);
			Console.WriteLine();
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
			else break;
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

internal readonly record struct SourceFileInfo(string FilePath, FileNode Ast, ISource Source);
internal readonly record struct ResolvedSourceFileInfo(string FilePath, ResolvedFileNode Ast, ISource Source);