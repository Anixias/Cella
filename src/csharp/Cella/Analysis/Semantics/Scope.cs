using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Cella.Analysis.Semantics.Symbols;

namespace Cella.Analysis.Semantics;

/// <summary>
/// An object that relates <see cref="SymbolTable"/> instances in an ancestry hierarchy. Thread-safe.
/// </summary>
public sealed class Scope : IEnumerable<Scope>
{
	private readonly object accessLock = new();

	public Scope? Parent
	{
		get
		{
			lock (accessLock)
				return parent;
		}
	}

	private readonly List<Scope> childScopes = [];
	private readonly Scope? parent;
	private readonly SymbolTable symbolTable = new();

	public Scope(Scope? parent = null)
	{
		this.parent = parent;
		parent?.AddChildScope(this);
	}
	
	public void AddSymbol(ISymbol symbol)
	{
		lock (accessLock)
			symbolTable.Add(symbol);
	}
	
	public void AddOrReplaceSymbol(ISymbol symbol)
	{
		lock (accessLock)
			symbolTable.AddOrReplace(symbol);
	}
	
	public ISymbol? LookupSymbol(string name)
	{
		lock (accessLock)
		{
			if (symbolTable.Lookup(name) is { } symbol)
				return symbol;

			return parent?.LookupSymbol(name);
		}
	}
	
	/// <summary>
	/// Searches recursively for a symbol matching the given name and type
	/// </summary>
	/// <param name="name">The name of the symbol to search for</param>
	/// <param name="symbol">Contains <see langword="null"/> if the symbol does not exist or is not of the given type;
	/// else, contains the symbol</param>
	/// <param name="existingSymbol">The symbol found by name; it may not be of the desired type</param>
	/// <typeparam name="T">The symbol type to filter for</typeparam>
	/// <returns>
	/// <see langword="true"/> if the symbol exists of the given type; <see langword="false"/> otherwise
	/// </returns>
	public bool LookupSymbol<T>(string name, out T? symbol, [NotNullWhen(true)] out ISymbol? existingSymbol)
		where T : class
	{
		symbol = null;
		existingSymbol = null;
		
		lock (accessLock)
		{
			if (symbolTable.Lookup(name) is not { } genericSymbol)
				return parent?.LookupSymbol(name, out symbol, out existingSymbol) ?? false;

			existingSymbol = genericSymbol;
			if (genericSymbol is not T typedSymbol)
				return false;

			symbol = typedSymbol;
			return true;
		}
	}
	
	public IEnumerator<Scope> GetEnumerator()
	{
		lock (accessLock)
			return childScopes.GetEnumerator();
	}
	
	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
	
	private void AddChildScope(Scope scope)
	{
		lock (accessLock)
			childScopes.Add(scope);
	}
}