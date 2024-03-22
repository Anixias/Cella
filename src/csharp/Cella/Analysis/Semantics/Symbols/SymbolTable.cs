using System.Collections;

namespace Cella.Analysis.Semantics.Symbols;

/// <summary>
/// An object that stores <see cref="ISymbol"/> instances by name. Thread-safe.
/// </summary>
public sealed class SymbolTable : IEnumerable<KeyValuePair<string, ISymbol>>
{
	private readonly object accessLock = new();
	
	public IReadOnlyCollection<string> Keys
	{
		get
		{
			lock (accessLock)
				return symbols.Keys;
		}
	}

	public IReadOnlyCollection<ISymbol> Values
	{
		get
		{
			lock (accessLock)
				return symbols.Values;
		}
	}

	private readonly Dictionary<string, ISymbol> symbols = new();
	
	public bool TryAdd(ISymbol symbol)
	{
		lock (accessLock)
			return symbols.TryAdd(symbol.Name, symbol);
	}
	
	public void Add(ISymbol symbol)
	{
		lock (accessLock)
			symbols.Add(symbol.Name, symbol);
	}

	/// <summary>
	/// Adds the given symbol to the <see cref="SymbolTable"/> if it does not already exist; else, overwrites the
	/// existing symbol.
	/// </summary>
	/// <param name="symbol">The symbol to add</param>
	public void AddOrReplace(ISymbol symbol)
	{
		lock (accessLock)
			symbols[symbol.Name] = symbol;
	}
	
	public bool Contains(string name)
	{
		lock (accessLock)
			return symbols.ContainsKey(name);
	}
	
	public ISymbol? Lookup(string name)
	{
		lock (accessLock)
			return symbols.GetValueOrDefault(name);
	}
	
	public IEnumerator<KeyValuePair<string, ISymbol>> GetEnumerator()
	{
		lock (accessLock)
			return symbols.GetEnumerator();
	}
	
	IEnumerator IEnumerable.GetEnumerator()
	{
		lock (accessLock)
			return GetEnumerator();
	}
	
	public void Clear()
	{
		lock (accessLock)
			symbols.Clear();
	}
	
	public SymbolTable Duplicate()
	{
		var result = new SymbolTable();
		
		lock (accessLock)
		{
			foreach (var (name, symbol) in symbols)
			{
				result.symbols.Add(name, symbol);
			}
		}
		
		return result;
	}
}