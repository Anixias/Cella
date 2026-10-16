using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class Scope(Scope? parent = null)
{
	public Scope? Parent { get; } = parent;
	public IReadOnlyCollection<Symbol> Symbols => _symbols.Values;
	
	private readonly Dictionary<string, Symbol> _symbols = [];
	private readonly Dictionary<string, LabelSymbol> _labels = [];
	
	public void Define(Symbol symbol)
	{
		if (symbol is LabelSymbol label)
			_labels[label.Name] = label;
		else if (symbol.Name != "_")
			_symbols[symbol.Name] = symbol;
	}
	
	public void Define(string name, Symbol symbol) => _symbols[name] = symbol;
	
	public Symbol? Resolve(string name) => _symbols.GetValueOrDefault(name) ?? Parent?.Resolve(name);
	
	public LabelSymbol? ResolveLabel(string name) => _labels.GetValueOrDefault(name) ?? Parent?.ResolveLabel(name);
	
	public IEnumerable<string> GetLabelNames() => _labels.Keys.Concat(Parent?.GetLabelNames() ?? []);
	
	public Scope CreateChild() => new(this);
}