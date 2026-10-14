using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class ModuleIndex
{
	private readonly Dictionary<string, ModulePathSymbol> _paths = [];
	private readonly Dictionary<Symbol, FileSymbol> _files = [];
	private readonly Dictionary<Symbol, FileSymbol> _dependencyFiles = [];
	private readonly Dictionary<Symbol, ModuleSymbol> _modules = [];
	private readonly Dictionary<Symbol, string> _assemblies = [];
	
	public ModuleIndex(string assemblyName, SymbolTable symbolTable, IEnumerable<AssemblySymbol> dependencies,
		IEnumerable<AssemblySymbol> indirectDependencies)
	{
		AssemblyName = assemblyName;
		Root = new ModulePathSymbol(string.Empty, string.Empty, null);
		_paths[string.Empty] = Root;
		
		Add(symbolTable, assemblyName, true);
		foreach (var dependency in dependencies)
		{
			Add(dependency.SymbolTable, dependency.Name, false);
			MapFiles(dependency.SymbolTable, _dependencyFiles);
		}
		
		foreach (var dependency in indirectDependencies)
		{
			Record(dependency.SymbolTable, dependency.Name);
			MapFiles(dependency.SymbolTable, _dependencyFiles);
		}
		
		MapFiles(symbolTable, _files);
	}
	
	public string AssemblyName { get; }
	
	private static void MapFiles(SymbolTable symbolTable, Dictionary<Symbol, FileSymbol> files)
	{
		foreach (var file in symbolTable.ModuleSymbols.Values.SelectMany(static module => module.Files))
			foreach (var symbol in file.Symbols.Values.SelectMany(static symbols => symbols))
				files[symbol] = file;
	}
	
	public ModulePathSymbol Root { get; }
	
	public ModulePathSymbol? Find(string path) => _paths.GetValueOrDefault(path);
	
	public static Visibility GetVisibility(Symbol symbol) =>
		symbol is IExportable exportable ? exportable.Visibility : Visibility.Public;
	
	public ModuleSymbol? FindModule(Symbol symbol) => _modules.GetValueOrDefault(symbol);
	
	public string? FindAssembly(Symbol symbol) => _assemblies.GetValueOrDefault(symbol);
	
	public string? FindPrivateFile(Symbol symbol) =>
		GetVisibility(symbol) == Visibility.Private &&
		(_files.GetValueOrDefault(symbol) ?? _dependencyFiles.GetValueOrDefault(symbol)) is { } file
			? file.Name.Replace('\\', '/')
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
	
	private void Add(SymbolTable symbolTable, string assemblyName, bool isLocal)
	{
		foreach (var (name, module) in symbolTable.ModuleSymbols)
		{
			var path = GetOrAdd([..name.Parts.Select(static part => part.Text)], name.Parts.Length);
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
	
	private ModulePathSymbol GetOrAdd(List<string> parts, int count)
	{
		var path = string.Join('.', parts.Take(count));
		if (_paths.TryGetValue(path, out var existing))
			return existing;
		
		var parent = GetOrAdd(parts, count - 1);
		var symbol = new ModulePathSymbol(parts[count - 1], path, parent);
		parent.Children[symbol.Name] = symbol;
		_paths[path] = symbol;
		return symbol;
	}
}