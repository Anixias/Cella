using System.Collections.Immutable;
using System.Text;
using Cella.Compiler.Linking;
using Cella.Compiler.Projects;
using Cella.Core.Binding;
using Cella.Core.CodeGen;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Diagnostics;

namespace Cella.Compiler.Building;

public sealed partial class Builder(BuildOptions options, IBuildOutput output)
{
	private static string StandardLibraryDirectory => Path.Combine(AppContext.BaseDirectory, "lib", "std");
	
	private static IEnumerable<string> FindStandardLibrary(StandardLibrary use) => use switch
	{
		StandardLibrary.Full =>
		[
			Path.Combine(StandardLibraryDirectory, "core", "stdCore.celp"),
			Path.Combine(StandardLibraryDirectory, "os", "std.celp")
		],
		StandardLibrary.Core => [Path.Combine(StandardLibraryDirectory, "core", "stdCore.celp")],
		_ => []
	};
	
	private readonly List<ProjectCheck> _projects = [];
	private readonly List<ProjectDiagnostic> _problems = [];
	private readonly HashSet<string> _unchecked = [];
	private readonly Dictionary<string, string> _outputDirectories = [];
	private string? entryProjectPath;
	private IReadOnlyDictionary<string, string> openFiles = new Dictionary<string, string>();
	private CompletionProbe? probe;
	private bool generatesCode;
	
	public async Task<BuildResult> CompleteAsync(string sourcePath, IReadOnlyDictionary<string, string> unsavedFiles,
		CompletionProbe completion, CancellationToken ct = default)
	{
		probe = completion;
		try
		{
			return await BuildAsync(sourcePath, unsavedFiles, ct);
		}
		finally
		{
			probe = null;
		}
	}
	
	public async Task<BuildResult> BuildAsync(string sourcePath,
		IReadOnlyDictionary<string, string>? unsavedFiles = null, CancellationToken ct = default)
	{
		_projects.Clear();
		_problems.Clear();
		_unchecked.Clear();
		openFiles = unsavedFiles ?? new Dictionary<string, string>();
		if (!Directory.Exists(sourcePath) && !File.Exists(sourcePath))
		{
			output.WriteLine($"'{sourcePath}' not found");
			return Fail();
		}
		
		// Phase 0a: Project collection
		var projectPaths = new HashSet<string>();
		
		if (Directory.Exists(sourcePath))
			projectPaths.UnionWith(CellaProject.FindProjects(sourcePath));
		else if (File.Exists(sourcePath) && !CellaProject.IsProjectFile(sourcePath))
		{
			output.WriteLine($"'{sourcePath}' is not a project file");
			return Fail();
		}
		else if (File.Exists(sourcePath))
			projectPaths.Add(sourcePath);
		
		if (projectPaths.Count == 0)
		{
			output.WriteLine($"No projects found in '{sourcePath}'");
			return Fail();
		}
		
		// Phase 0b: Dependency graph
		var dependencyGraph = await MapDependenciesAsync(projectPaths, ct);
		
		if (dependencyGraph.FindCycle() is [_, ..] cycle)
		{
			var names = cycle.Append(cycle[0]).Select(static project => Path.GetFileName(project.FilePath));
			_problems.Add(ProjectDiagnostic.About($"Project references form a cycle: {string.Join(" -> ", names)}",
				cycle.Select(static project => project.FilePath)));
			
			ReportProblems();
			return Fail();
		}
		
		var projectDependencies = dependencyGraph.GetResolutionOrder().ToDictionary(static p => p,
			p => dependencyGraph.GetDependencies(p).ToImmutableArray());
		
		AssignBundledOutputs(projectDependencies);
		if (options.Entry is not null)
		{
			var used = projectDependencies.Values.SelectMany(static items => items).ToHashSet();
			if (projectDependencies.Keys.Where(project => !used.Contains(project)).ToList() is not [var root])
			{
				output.WriteLine("'--entry' needs exactly one project to build");
				return Fail();
			}
			
			entryProjectPath = root.FilePath;
		}
		
		var builtInFlags = BuildFlags.Create(options.Target, options.OptimizeMode);
		var linkPreferences = FindLinkPreferences(projectDependencies);
		foreach (var (project, preference) in linkPreferences)
		{
			if (project.Project is { Referenceable: true, OutputType: var type } && type.IsExecutable() &&
			    preference == SystemLinkPreference.Static)
				_problems.Add(ProjectDiagnostic.In(project.FilePath,
					"A referenceable executable can't link the C runtime statically"));
		}
		
		ValidateFlags(projectDependencies.Keys, builtInFlags, options.FlagOverrides);
		ValidateOutputNames(projectDependencies);
		ValidateLibraryNames(projectDependencies.Keys);
		ReportProblems();
		
		_unchecked.UnionWith(_problems.Where(static problem => problem.StopsChecking)
			.SelectMany(static problem => problem.Projects));
		
		generatesCode = !HasErrors;
		var result = await BuildProjectsAsync(projectDependencies, linkPreferences, ct);
		return result with { Succeeded = result.Succeeded && !HasErrors };
	}
	
	private bool HasErrors => _problems.Any(static problem => problem.Severity == DiagnosticSeverity.Error);
	
	private void ReportProblems()
	{
		foreach (var problem in _problems)
			output.WriteLine(problem.Text);
	}
	
	private BuildResult Fail() => new(false, [.._projects], [.._problems]);
	
	private void AssignBundledOutputs(Dictionary<ProjectInfo, ImmutableArray<ProjectInfo>> dependencies)
	{
		_outputDirectories.Clear();
		var library = GetCanonicalPath(StandardLibraryDirectory) + Path.DirectorySeparatorChar;
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		var used = dependencies.Values.SelectMany(static items => items).ToHashSet();
		foreach (var root in dependencies.Keys.Where(project => !used.Contains(project))
			         .OrderBy(static project => project.FilePath, StringComparer.Ordinal))
		{
			var reached = new HashSet<ProjectInfo>();
			var pending = new Stack<ProjectInfo>(dependencies.GetValueOrDefault(root, []));
			while (pending.TryPop(out var next))
			{
				if (!reached.Add(next))
					continue;
				
				if (next.FilePath.StartsWith(library, comparison))
					_outputDirectories.TryAdd(next.FilePath, Path.Combine(root.Directory, "obj", "std", next.Name));
				
				foreach (var dependency in dependencies.GetValueOrDefault(next, []))
					pending.Push(dependency);
			}
		}
	}
	
	private string GetEntryDirectory(ProjectInfo project) =>
		Path.Combine(project.Directory, "obj", "entry", options.Entry!);
	
	private string GetOutputDirectory(ProjectInfo project) =>
		_outputDirectories.GetValueOrDefault(project.FilePath, project.Directory);
	
	private static string GetOutputBaseName(ProjectInfo project) => project.Project.AssemblyName ?? project.Name;
	
	private Dictionary<ProjectInfo, SystemLinkPreference> FindLinkPreferences(
		Dictionary<ProjectInfo, ImmutableArray<ProjectInfo>> dependencies)
	{
		var used = dependencies.Values.SelectMany(static items => items).ToHashSet();
		var programs = dependencies.Keys.ToDictionary(static project => project, static _ => new List<ProjectInfo>());
		foreach (var root in dependencies.Keys.Where(project => !used.Contains(project)))
		{
			var reached = new HashSet<ProjectInfo>();
			var pending = new Stack<ProjectInfo>([root]);
			while (pending.TryPop(out var next))
			{
				if (!reached.Add(next))
					continue;
				
				programs[next].Add(root);
				foreach (var dependency in dependencies.GetValueOrDefault(next, []))
					pending.Push(dependency);
			}
			
			if (root.Project.SystemLinkPreference != SystemLinkPreference.Static)
				continue;
			
			foreach (var library in reached.Where(project => project != root &&
			                                                 project.Project.OutputType ==
			                                                 ProjectOutputType.SharedLibrary)
				         .OrderBy(static project => project.FilePath, StringComparer.Ordinal))
			{
				_problems.Add(ProjectDiagnostic.About(
					$"'{root.FilePath}' links the C runtime statically, so it can't use the shared library " +
					$"'{library.FilePath}'", root.FilePath, library.FilePath));
			}
		}
		
		var preferences = new Dictionary<ProjectInfo, SystemLinkPreference>();
		foreach (var (project, roots) in programs)
		{
			var chosen = roots.Select(static root => root.Project.SystemLinkPreference).Distinct().ToList();
			if (chosen.Count > 1)
			{
				var names = roots.Select(static root => $"'{root.FilePath}'").Order(StringComparer.Ordinal).ToList();
				_problems.Add(ProjectDiagnostic.About(
					$"'{project.FilePath}' is used by {string.Join(", ", names[..^1])} and {names[^1]}, " +
					"which link the C runtime differently",
					[project.FilePath, ..roots.Select(static root => root.FilePath)]));
			}
			
			preferences[project] = chosen.FirstOrDefault(project.Project.SystemLinkPreference);
		}
		
		return preferences;
	}
	
	private void ValidateOutputNames(Dictionary<ProjectInfo, ImmutableArray<ProjectInfo>> dependencies)
	{
		var reported = new HashSet<string>();
		foreach (var project in dependencies.Keys)
		{
			var closure = new HashSet<ProjectInfo>();
			var pending = new Stack<ProjectInfo>([project]);
			while (pending.TryPop(out var next))
			{
				if (!closure.Add(next))
					continue;
				
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
					_problems.Add(ProjectDiagnostic.About(
						$"Projects {described} {(paths.Count == 2 ? "both" : "all")} build '{sameName.Key}'",
						sameName.Select(static clash => clash.FilePath)));
			}
		}
	}
	
	private void ValidateLibraryNames(IEnumerable<ProjectInfo> projects)
	{
		foreach (var project in projects.OrderBy(static project => project.FilePath, StringComparer.Ordinal))
		{
			var name = GetOutputBaseName(project);
			if (project.Project.OutputType != ProjectOutputType.SharedLibrary || Ascii.IsValid(name))
				continue;
			
			_problems.Add(ProjectDiagnostic.About(
				$"The shared library name '{name}' in '{project.FilePath}' must be ASCII", project.FilePath));
		}
	}
	
	private Dictionary<string, bool> CreateFlags(CellaProject project)
	{
		var flags = BuildFlags.Create(options.Target, options.OptimizeMode);
		flags["boundsChecks"] = BuildFlags.ChecksBounds(project);
		flags["overflowChecks"] = BuildFlags.ChecksOverflow(project, options.OptimizeMode);
		foreach (var (name, value) in project.Flags ?? [])
			flags[name] = options.FlagOverrides.GetValueOrDefault(name, value);
		
		return flags;
	}
	
	private void ValidateFlags(IEnumerable<ProjectInfo> projects, IReadOnlyDictionary<string, bool> builtIn,
		IReadOnlyDictionary<string, bool> overrides)
	{
		var declared = new HashSet<string>();
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
				
				_problems.Add(ProjectDiagnostic.In(project.FilePath, problem));
			}
		}
		
		foreach (var name in overrides.Keys.Where(name => builtIn.ContainsKey(name) || !declared.Contains(name)))
		{
			_problems.Add(ProjectDiagnostic.About(builtIn.ContainsKey(name)
				? $"Cannot set built-in flag '{name}'"
				: $"Flag '{name}' not found"));
		}
	}
	
	private void CheckVersion(string path, CellaProject project)
	{
		if (project.CellaVersion is not { } pin || SemanticVersion.Parse(pin) is not { } version)
			return;
		
		var comparison = version.CompareTo(SemanticVersion.Current);
		if (comparison > 0)
			_problems.Add(ProjectDiagnostic.In(path, $"Pinned to Cella {pin}, newer than this compiler"));
		else if (comparison < 0)
			_problems.Add(ProjectDiagnostic.In(path, $"Pinned to Cella {pin}, older than this compiler") with
			{
				Severity = DiagnosticSeverity.Warning
			});
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
	
	private async Task<DependencyGraph<ProjectInfo>> MapDependenciesAsync(HashSet<string> projectPaths,
		CancellationToken ct)
	{
		var projectLookup = new Dictionary<string, ProjectInfo>();
		var projectDependencies = new Dictionary<ProjectInfo, HashSet<string>>();
		var projectQueue = new Queue<string>(projectPaths.Select(GetCanonicalPath));
		var processedPaths = new HashSet<string>();
		var graph = new DependencyGraph<ProjectInfo>();
		var failed = new HashSet<string>();
		var missingLibrary = new List<string>();
		
		// Collect projects
		while (projectQueue.Count > 0)
		{
			var path = projectQueue.Dequeue();
			if (!processedPaths.Add(path))
				continue;
			
			CellaProject? loaded;
			await using (var stream = await OpenProjectFileAsync(path, ct))
				loaded = await CellaProject.LoadAsync(stream, path, _problems);
			
			if (loaded is null)
			{
				failed.Add(path);
				continue;
			}
			
			if (CellaProject.FindSiblingProjects(path).Select(static name => $"'{name}'").ToList() is [_, ..] siblings)
				_problems.Add(ProjectDiagnostic.In(path, $"Cannot share a directory with {string.Join(", ", siblings)}")
					with
					{
						StopsChecking = true
					});
			
			var project = loaded.Resolve(path, CreateFlags(loaded), _problems);
			CheckVersion(path, project);
			var projectDirectory = Path.GetDirectoryName(path) ?? string.Empty;
			var projectName = Path.GetFileNameWithoutExtension(path);
			var projectInfo = new ProjectInfo(path, projectDirectory, projectName, project);
			graph.Add(projectInfo);
			projectLookup[path] = projectInfo;
			
			foreach (var library in FindStandardLibrary(project.StandardLibrary))
			{
				if (!File.Exists(library))
				{
					if (missingLibrary.LastOrDefault() != path)
						missingLibrary.Add(path);
					
					continue;
				}
				
				var libraryPath = GetCanonicalPath(library);
				projectDependencies.GetOrAdd(projectInfo).Add(libraryPath);
				if (!processedPaths.Contains(libraryPath))
					projectQueue.Enqueue(libraryPath);
			}
			
			// Queue dependencies to be collected
			if (project.ProjectReferences is not { } references)
				continue;
			
			foreach (var alias in references.Select(static reference => reference.Alias).OfType<string>())
			{
				if (!BuildFlags.IsValidName(alias) || NativeSymbols.IsBuiltInName(alias))
					_problems.Add(ProjectDiagnostic.In(path, $"'{alias}' is not a valid alias"));
			}
			
			foreach (var alias in references.Select(static reference => reference.Alias).OfType<string>()
				         .GroupBy(static alias => alias).Where(static sameAlias => sameAlias.Count() > 1))
				_problems.Add(ProjectDiagnostic.In(path, $"'{alias.Key}' names more than one project reference"));
			
			foreach (var reference in references)
			{
				var dependencyPath = GetCanonicalPath(Path.Combine(projectDirectory, reference.Path!));
				if (!File.Exists(dependencyPath))
				{
					_problems.Add(ProjectDiagnostic.In(path, $"Project '{reference.Path}' not found") with
					{
						StopsChecking = true
					});
					
					continue;
				}
				
				if (!CellaProject.IsProjectFile(dependencyPath))
				{
					_problems.Add(ProjectDiagnostic.In(path, $"'{reference.Path}' is not a project file") with
					{
						StopsChecking = true
					});
					
					continue;
				}
				
				projectDependencies.GetOrAdd(projectInfo).Add(dependencyPath);
				
				if (!processedPaths.Contains(dependencyPath))
					projectQueue.Enqueue(dependencyPath);
			}
		}
		
		if (missingLibrary.Count > 0)
			_problems.Add(ProjectDiagnostic.About($"Standard library not found in '{StandardLibraryDirectory}'",
					missingLibrary) with
				{
					StopsChecking = true
				});
		
		// Map project dependencies
		foreach (var (project, dependencies) in projectDependencies)
		{
			foreach (var dependency in dependencies)
			{
				if (!projectLookup.TryGetValue(dependency, out var target))
				{
					if (failed.Contains(dependency))
						_unchecked.Add(project.FilePath);
					
					continue;
				}
				
				if (target.Project.OutputType.IsExecutable() && target.Project.Referenceable != true)
					_problems.Add(ProjectDiagnostic.In(project.FilePath,
							$"'{target.FilePath}' is an executable without '{nameof(CellaProject.Referenceable)} = true'")
						with
						{
							StopsChecking = true
						});
				
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
		public RuntimeChecks Checks { get; init; }
		public IReadOnlySet<GlobalSymbol> ExportedStatics { get; init; } = ImmutableHashSet<GlobalSymbol>.Empty;
		public bool HasStaticDrops { get; init; }
		public IReadOnlySet<Symbol> Exports { get; init; } = ImmutableHashSet<Symbol>.Empty;
		public ImmutableArray<ExportName> ExportNames { get; init; } = [];
		public ImmutableArray<string> PlainNames { get; init; } = [];
		public ImmutableArray<string> ContainedLibraries { get; init; } = [];
		public ImmutableArray<string> LibraryFiles { get; init; } = [];
		public ImmutableArray<string> ImportedFunctions { get; init; } = [];
		public ImmutableArray<string> ImportedGlobals { get; init; } = [];
		public string? Directory { get; init; }
		public string? ProjectPath { get; init; }
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
	
	private static ImmutableArray<AssemblyInfo> OrderForDrops(ImmutableArray<AssemblyInfo> dependencies)
	{
		var ordered = new List<AssemblyInfo>();
		var seen = new HashSet<AssemblySymbol>();
		Visit(dependencies);
		ordered.Reverse();
		return [..ordered];
		
		void Visit(ImmutableArray<AssemblyInfo> items)
		{
			foreach (var item in items)
			{
				if (!seen.Add(item.AssemblySymbol))
					continue;
				
				Visit(item.Dependencies);
				ordered.Add(item);
			}
		}
	}
	
	private static IEnumerable<Symbol> GetLibrarySymbols(AssemblyInfo library) =>
		library.AssemblySymbol.SignatureTable.Functions.Keys
			.Concat<Symbol>(library.AssemblySymbol.SignatureTable.Globals.Keys)
			.Concat(library.ExportedStatics);
	
	private static Dictionary<string, string> FindAliases(ProjectInfo project,
		ImmutableArray<AssemblyInfo> dependencies)
	{
		var aliases = new Dictionary<string, string>();
		foreach (var reference in project.Project.ProjectReferences ?? [])
		{
			if (reference is not { Path: { } path, Alias: { } alias })
				continue;
			
			var target = GetCanonicalPath(Path.Combine(project.Directory, path));
			if (dependencies.FirstOrDefault(dependency => dependency.ProjectPath == target) is
			    { AssemblySymbol: { } symbol })
				aliases[symbol.Name] = alias;
		}
		
		return aliases;
	}
}