using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class Scope(Scope? parent = null)
{
	public Scope? Parent { get; } = parent;
	
	private readonly Dictionary<string, Symbol> _symbols = [];
	
	public void Define(Symbol symbol) => _symbols[symbol.Name] = symbol;
	public Symbol? Resolve(string name) => _symbols.GetValueOrDefault(name) ?? Parent?.Resolve(name);
	
	public Scope CreateChild() => new(this);
}