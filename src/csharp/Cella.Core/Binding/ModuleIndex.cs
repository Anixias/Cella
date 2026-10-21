using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class ModuleIndex
{
	private readonly Dictionary<string, ModulePathSymbol> _paths = [];
	private readonly HashSet<ModulePathSymbol> _localPaths;
	private readonly Dictionary<ModulePathSymbol, List<ModuleSymbol>> _pathModules = [];
	private readonly Dictionary<Symbol, FileSymbol> _files = [];
	private readonly Dictionary<Symbol, FileSymbol> _dependencyFiles = [];
	private readonly Dictionary<Symbol, ModuleSymbol> _modules = [];
	private readonly Dictionary<Symbol, string> _assemblies = [];
	private readonly HashSet<string> _projectFiles = [];
	
	public ModuleIndex(string assemblyName, SymbolTable symbolTable, IEnumerable<AssemblySymbol> dependencies,
		IEnumerable<AssemblySymbol> indirectDependencies, IReadOnlyDictionary<string, string> aliases)
	{
		AssemblyName = assemblyName;
		Root = new ModulePathSymbol(string.Empty, string.Empty, null);
		_paths[string.Empty] = Root;
		
		Add(symbolTable, assemblyName, true, Root, _paths);
		_localPaths = [.._paths.Values];
		var aliasRoots = new HashSet<ModulePathSymbol>();
		var providers = new Dictionary<ModulePathSymbol, List<(string Project, ModulePathSymbol Module)>>();
		var roots = new List<(AssemblySymbol Dependency, string? Alias, ModulePathSymbol Root)>();
		foreach (var dependency in dependencies)
		{
			var alias = aliases.GetValueOrDefault(dependency.Name);
			var root = new ModulePathSymbol(alias ?? string.Empty, alias ?? string.Empty, null);
			Add(dependency.SymbolTable, dependency.Name, false, root, new() { [root.Path] = root });
			MapFiles(dependency.SymbolTable, _dependencyFiles);
			roots.Add((dependency, alias, root));
		}
		
		HashSet<string> moduleNames =
		[
			..Root.Children.Keys,
			..roots.Where(static entry => entry.Alias is null).SelectMany(static entry => entry.Root.Children.Keys)
		];
		
		AliasConflicts = [..roots.Select(static entry => entry.Alias).OfType<string>().Where(moduleNames.Contains)];
		foreach (var (dependency, alias, root) in roots)
		{
			if (alias is null)
				Collect(Root, root, dependency.Name, providers, aliasRoots);
			else if (Root.Children.TryAdd(alias, root))
				aliasRoots.Add(root);
		}
		
		foreach (var (path, sources) in providers)
			Merge(path, sources);
		
		foreach (var dependency in indirectDependencies)
		{
			Record(dependency.SymbolTable, dependency.Name);
			MapFiles(dependency.SymbolTable, _dependencyFiles);
		}
		
		MapFiles(symbolTable, _files);
	}
	
	public string AssemblyName { get; }
	
	public ImmutableArray<string> AliasConflicts { get; }
	
	private void MapFiles(SymbolTable symbolTable, Dictionary<Symbol, FileSymbol> files)
	{
		foreach (var file in symbolTable.ModuleSymbols.Values.SelectMany(static module => module.Files))
		{
			_projectFiles.Add(file.FullPath);
			foreach (var symbol in file.Symbols.Values.SelectMany(static symbols => symbols))
				files[symbol] = file;
		}
	}
	
	public bool IsReachable(TraitSymbol trait) => _projectFiles.Contains(trait.Node.SourceLocation.Source.FilePath);
	
	public ModulePathSymbol Root { get; }
	
	public ModulePathSymbol? Find(string path) => _paths.GetValueOrDefault(path);
	
	public IEnumerable<ModuleSymbol> GetModules(ModulePathSymbol path) =>
		_pathModules.GetValueOrDefault(path) ?? [];
	
	public bool IsLocal(ModulePathSymbol path) => _localPaths.Contains(path);
	
	private static string Join(string parent, string name) => parent.Length == 0 ? name : $"{parent}.{name}";
	
	private void Collect(ModulePathSymbol target, ModulePathSymbol source, string project,
		Dictionary<ModulePathSymbol, List<(string Project, ModulePathSymbol Module)>> providers,
		HashSet<ModulePathSymbol> aliasRoots)
	{
		foreach (var (name, child) in source.Children)
		{
			if (!target.Children.TryGetValue(name, out var merged))
			{
				merged = new ModulePathSymbol(name, Join(target.Path, name), target);
				target.Children[name] = merged;
				_paths[merged.Path] = merged;
			}
			else if (aliasRoots.Contains(merged))
				continue;
			
			providers.GetOrAdd(merged).Add((project, child));
			Collect(merged, child, project, providers, aliasRoots);
		}
	}
	
	private void Merge(ModulePathSymbol path, List<(string Project, ModulePathSymbol Module)> sources)
	{
		HashSet<string> own = _localPaths.Contains(path) ? [..path.Members.Keys, ..path.PrivateMembers.Keys] : [];
		foreach (var (_, module) in sources)
		{
			Copy(module.Members, path.Members, own);
			Copy(module.PrivateMembers, path.PrivateMembers, own);
			if (_pathModules.TryGetValue(module, out var modules))
				_pathModules.GetOrAdd(path).AddRange(modules);
		}
	}
	
	private static void Copy(Dictionary<string, List<Symbol>> from, Dictionary<string, List<Symbol>> to,
		HashSet<string> own)
	{
		foreach (var (name, members) in from)
		{
			if (!own.Contains(name))
				to.GetOrAdd(name).AddRange(members);
		}
	}
	
	public static Visibility GetVisibility(Symbol symbol) =>
		symbol is IExportable exportable ? exportable.Visibility : Visibility.Public;
	
	public ModuleSymbol? FindModule(Symbol symbol) => _modules.GetValueOrDefault(symbol);
	
	public string? FindAssembly(Symbol symbol) => _assemblies.GetValueOrDefault(symbol);
	
	public string? FindPrivateFile(Symbol symbol) =>
		GetVisibility(symbol) == Visibility.Private &&
		(_files.GetValueOrDefault(symbol) ?? _dependencyFiles.GetValueOrDefault(symbol)) is { } file
			? file.Name
			: null;
	
	public bool IsVisible(Symbol symbol, FileSymbol from) => GetVisibility(symbol) switch
	{
		Visibility.Public => true,
		var visibility => _files.GetValueOrDefault(symbol) is { } file && visibility switch
		{
			Visibility.Private => file == from,
			Visibility.Module => file.Module == from.Module,
			_ => true
		}
	};
	
	public bool IsAccessible(TypeSymbol owner, Visibility visibility, FileSymbol from, TypeSymbol? within)
	{
		owner = owner.OriginalDefinition;
		if (visibility == Visibility.Module)
			return _files.GetValueOrDefault(owner)?.Module == from.Module;
		
		if (visibility != Visibility.Private)
			return true;
		
		if (_files.GetValueOrDefault(owner)?.Module != from.Module)
			return false;
		
		for (var type = within; type is not null; type = type.ContainingType)
		{
			if (type.OriginalDefinition == owner)
				return true;
		}
		
		return false;
	}
	
	private void Record(SymbolTable symbolTable, string assemblyName)
	{
		foreach (var module in symbolTable.ModuleSymbols.Values)
		{
			if (!symbolTable.SymbolsByModule.TryGetValue(module, out var members))
				continue;
			
			foreach (var symbol in members.Values.SelectMany(static symbols => symbols))
			{
				_modules[symbol] = module;
				_assemblies[symbol] = assemblyName;
			}
		}
	}
	
	private void Add(SymbolTable symbolTable, string assemblyName, bool isLocal, ModulePathSymbol root,
		Dictionary<string, ModulePathSymbol> paths)
	{
		foreach (var (name, module) in symbolTable.ModuleSymbols)
		{
			var path = GetOrAdd([..name.Parts.Select(static part => part.Text)], name.Parts.Length, root, paths);
			_pathModules.GetOrAdd(path).Add(module);
			if (!symbolTable.SymbolsByModule.TryGetValue(module, out var members))
				continue;
			
			foreach (var (memberName, symbols) in members)
			{
				foreach (var symbol in symbols)
				{
					var isVisible = isLocal || symbol is IExportable { Visibility: Visibility.Public };
					(isVisible ? path.Members : path.PrivateMembers).GetOrAdd(memberName).Add(symbol);
					_modules[symbol] = module;
					_assemblies[symbol] = assemblyName;
				}
			}
		}
	}
	
	private static ModulePathSymbol GetOrAdd(List<string> parts, int count, ModulePathSymbol root,
		Dictionary<string, ModulePathSymbol> paths)
	{
		if (count == 0)
			return root;
		
		var path = string.Join('.', parts.Take(count));
		var key = root.Path.Length == 0 ? path : $"{root.Path}.{path}";
		if (paths.TryGetValue(key, out var existing))
			return existing;
		
		var parent = GetOrAdd(parts, count - 1, root, paths);
		var symbol = new ModulePathSymbol(parts[count - 1], key, parent);
		parent.Children[symbol.Name] = symbol;
		paths[key] = symbol;
		return symbol;
	}
}