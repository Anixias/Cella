using System.Collections.Immutable;
using System.Text;
using Cella.Compiler.Linking;
using Cella.Compiler.Projects;
using Cella.Core.Binding;
using Cella.Core.CodeGen;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Compiler.Building;

public sealed partial class Builder(BuildOptions options, IBuildOutput output)
{
	public static string ToolchainsDirectory => Path.Combine(AppContext.BaseDirectory, "toolchains");
	
	private readonly List<ProjectCheck> _projects = [];
	private IReadOnlyDictionary<string, string> openFiles = new Dictionary<string, string>();
	private CompletionProbe? probe;
	
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
		var projectErrors = new List<string>();
		var dependencyGraph = await MapDependenciesAsync(projectPaths, projectErrors, ct);
		
		if (dependencyGraph.FindCycle() is [_, ..] cycle)
		{
			foreach (var error in projectErrors)
				output.WriteLine(error);
			
			var names = cycle.Append(cycle[0]).Select(static project => Path.GetFileName(project.FilePath));
			output.WriteLine($"Project references form a cycle: {string.Join(" -> ", names)}");
			return Fail();
		}
		
		var projectDependencies = dependencyGraph.GetResolutionOrder().ToDictionary(static p => p,
			p => dependencyGraph.GetDependencies(p).ToImmutableArray());
		
		var builtInFlags = BuildFlags.Create(options.Target, options.OptimizeMode);
		var linkPreferences = FindLinkPreferences(projectDependencies);
		foreach (var (project, preference) in linkPreferences ?? [])
		{
			if (project.Project is { Referenceable: true, OutputType: var type } && type.IsExecutable() &&
			    preference == SystemLinkPreference.Static)
				projectErrors.Add(
					$"{project.FilePath}: A referenceable executable can't link the C runtime statically");
		}
		
		var flagsValid = ValidateFlags(projectDependencies.Keys, builtInFlags, options.FlagOverrides) &
		                 ValidateOutputNames(projectDependencies) & ValidateLibraryNames(projectDependencies.Keys) &
		                 linkPreferences is not null;
		
		foreach (var error in projectErrors)
			output.WriteLine(error);
		
		if (!flagsValid || projectErrors.Count > 0)
			return Fail();
		
		return await BuildProjectsAsync(projectDependencies, linkPreferences!, ct);
	}
	
	private BuildResult Fail() => new(false, [.._projects]);
	
	private static string GetOutputBaseName(ProjectInfo project) => project.Project.AssemblyName ?? project.Name;
	
	private Dictionary<ProjectInfo, SystemLinkPreference>? FindLinkPreferences(
		Dictionary<ProjectInfo, ImmutableArray<ProjectInfo>> dependencies)
	{
		var used = dependencies.Values.SelectMany(static items => items).ToHashSet();
		var programs = dependencies.Keys.ToDictionary(static project => project, static _ => new List<ProjectInfo>());
		var isValid = true;
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
				output.WriteLine($"'{root.FilePath}' links the C runtime statically, so it can't use the shared " +
				                 $"library '{library.FilePath}'");
				
				isValid = false;
			}
		}
		
		var preferences = new Dictionary<ProjectInfo, SystemLinkPreference>();
		foreach (var (project, roots) in programs)
		{
			var chosen = roots.Select(static root => root.Project.SystemLinkPreference).Distinct().ToList();
			if (chosen.Count > 1)
			{
				var names = roots.Select(static root => $"'{root.FilePath}'").Order(StringComparer.Ordinal).ToList();
				output.WriteLine(
					$"'{project.FilePath}' is used by {string.Join(", ", names[..^1])} and {names[^1]}, " +
					"which link the C runtime differently");
				
				isValid = false;
			}
			
			preferences[project] = chosen.FirstOrDefault(project.Project.SystemLinkPreference);
		}
		
		return isValid ? preferences : null;
	}
	
	private bool ValidateOutputNames(Dictionary<ProjectInfo, ImmutableArray<ProjectInfo>> dependencies)
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
					output.WriteLine(
						$"Projects {described} {(paths.Count == 2 ? "both" : "all")} build '{sameName.Key}'");
			}
		}
		
		return reported.Count == 0;
	}
	
	private bool ValidateLibraryNames(IEnumerable<ProjectInfo> projects)
	{
		var isValid = true;
		foreach (var project in projects.OrderBy(static project => project.FilePath, StringComparer.Ordinal))
		{
			var name = GetOutputBaseName(project);
			if (project.Project.OutputType != ProjectOutputType.SharedLibrary || Ascii.IsValid(name))
				continue;
			
			output.WriteLine($"The shared library name '{name}' in '{project.FilePath}' must be ASCII");
			isValid = false;
		}
		
		return isValid;
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
	
	private bool ValidateFlags(IEnumerable<ProjectInfo> projects, IReadOnlyDictionary<string, bool> builtIn,
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
				
				output.WriteLine($"{project.FilePath}: {problem}");
				isValid = false;
			}
		}
		
		foreach (var name in overrides.Keys.Where(name => builtIn.ContainsKey(name) || !declared.Contains(name)))
		{
			output.WriteLine(builtIn.ContainsKey(name)
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
	
	private async Task<DependencyGraph<ProjectInfo>> MapDependenciesAsync(HashSet<string> projectPaths,
		List<string> errors, CancellationToken ct)
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
			await using (var stream = await OpenProjectFileAsync(path, ct))
				loaded = await CellaProject.LoadAsync(stream, path, errors);
			
			if (loaded is null)
				continue;
			
			if (CellaProject.FindSiblingProjects(path).Select(static name => $"'{name}'").ToList() is [_, ..] siblings)
				errors.Add($"{path}: Cannot share a directory with {string.Join(", ", siblings)}");
			
			var project = loaded.Resolve(path, CreateFlags(loaded), errors);
			var projectDirectory = Path.GetDirectoryName(path) ?? string.Empty;
			var projectName = Path.GetFileNameWithoutExtension(path);
			var projectInfo = new ProjectInfo(path, projectDirectory, projectName, project);
			graph.Add(projectInfo);
			projectLookup[path] = projectInfo;
			
			// Queue dependencies to be collected
			if (project.ProjectReferences is not { } references)
				continue;
			
			foreach (var alias in references.Select(static reference => reference.Alias).OfType<string>())
			{
				if (!BuildFlags.IsValidName(alias) || NativeSymbols.IsBuiltInName(alias))
					errors.Add($"{path}: '{alias}' is not a valid alias");
			}
			
			foreach (var alias in references.Select(static reference => reference.Alias).OfType<string>()
				         .GroupBy(static alias => alias).Where(static sameAlias => sameAlias.Count() > 1))
				errors.Add($"{path}: '{alias.Key}' names more than one project reference");
			
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
				if (!projectLookup.TryGetValue(dependency, out var target))
					continue;
				
				if (target.Project.OutputType.IsExecutable() && target.Project.Referenceable != true)
					errors.Add($"{project.FilePath}: '{target.FilePath}' is an executable without " +
					           $"'{nameof(CellaProject.Referenceable)} = true'");
				
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