using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class ModuleIndex
{
	private readonly Dictionary<string, ModulePathSymbol> _paths = [];
	private readonly Dictionary<Symbol, FileSymbol> _files = [];
	private readonly Dictionary<Symbol, FileSymbol> _dependencyFiles = [];
	private readonly Dictionary<Symbol, ModuleSymbol> _modules = [];
	
	public ModuleIndex(SymbolTable symbolTable, IEnumerable<SymbolTable> dependencies)
	{
		Root = new ModulePathSymbol(string.Empty, null);
		_paths[string.Empty] = Root;
		
		Add(symbolTable, true);
		foreach (var dependency in dependencies)
		{
			Add(dependency, false);
			MapFiles(dependency, _dependencyFiles);
		}
		
		MapFiles(symbolTable, _files);
	}
	
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
	
	private void Add(SymbolTable symbolTable, bool isLocal)
	{
		foreach (var (name, module) in symbolTable.ModuleSymbols)
		{
			var path = GetOrAdd(name.Text);
			if (!symbolTable.SymbolsByModule.TryGetValue(module, out var members))
				continue;
			
			foreach (var (memberName, symbols) in members)
			{
				foreach (var symbol in symbols)
				{
					var isVisible = isLocal || symbol is IExportable { Visibility: Visibility.Public };
					(isVisible ? path.Members : path.PrivateMembers).GetOrAdd(memberName).Add(symbol);
					_modules[symbol] = module;
				}
			}
		}
	}
	
	private ModulePathSymbol GetOrAdd(string path)
	{
		if (_paths.TryGetValue(path, out var existing))
			return existing;
		
		var separator = path.LastIndexOf('.');
		var parent = separator < 0 ? Root : GetOrAdd(path[..separator]);
		var symbol = new ModulePathSymbol(path, parent);
		parent.Children[symbol.Name] = symbol;
		_paths[path] = symbol;
		return symbol;
	}
}