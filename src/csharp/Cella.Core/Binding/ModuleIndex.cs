using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class ModuleIndex
{
	private readonly Dictionary<string, ModulePathSymbol> _paths = [];
	
	public ModuleIndex(SymbolTable symbolTable, IEnumerable<SymbolTable> dependencies)
	{
		Root = new ModulePathSymbol(string.Empty, null);
		_paths[string.Empty] = Root;
		
		Add(symbolTable, true);
		foreach (var dependency in dependencies)
			Add(dependency, false);
	}
	
	public ModulePathSymbol Root { get; }
	
	public ModulePathSymbol? Find(string path) => _paths.GetValueOrDefault(path);
	
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