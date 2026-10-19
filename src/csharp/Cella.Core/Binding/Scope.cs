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
	
	public Symbol? Resolve(string name) =>
		GetChain().Select(scope => scope._symbols.GetValueOrDefault(name))
			.FirstOrDefault(static symbol => symbol is not null);
	
	public LabelSymbol? ResolveLabel(string name) =>
		GetChain().Select(scope => scope._labels.GetValueOrDefault(name))
			.FirstOrDefault(static label => label is not null);
	
	public IEnumerable<string> GetLabelNames() => GetChain().SelectMany(static scope => scope._labels.Keys);
	
	private IEnumerable<Scope> GetChain()
	{
		for (var scope = this; scope is not null; scope = scope.Parent)
			yield return scope;
	}
	
	public Scope CreateChild() => new(this);
}