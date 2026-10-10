using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class OpenLocal(ISyntaxNode declaration, Token identifier, IExpressionNode initializer,
	ResolutionContext context)
{
	public ISyntaxNode Declaration { get; } = declaration;
	public Token Identifier { get; } = identifier;
	public IExpressionNode Initializer { get; } = initializer;
	public ResolutionContext Context { get; } = context;
	public bool IsCaseName { get; init; }
	public bool IsCounter { get; init; }
	public OpenLocal? Owner { get; init; }
	public ImmutableArray<BigInteger> Lengths { get; init; } = [];
	public TrackedLocal Tracked => new(Owner ?? this, Lengths);
}

public readonly record struct TrackedLocal(OpenLocal Local, ImmutableArray<BigInteger> Lengths)
{
	public bool IsWhole => Lengths.IsEmpty;
}

public enum LocalUseKind
{
	Read,
	Write,
	Cast,
	Link
}

public readonly record struct LocalUse(OpenLocal Local, LocalUseKind Kind, TypeSymbol? Type, OpenLocal? Other);

public readonly record struct LocalSettlement
(
	IReadOnlyDictionary<ISyntaxNode, TypeSymbol> Types,
	IReadOnlyList<(OpenLocal Local, ImmutableArray<TypeSymbol> Types)> Conflicts
);

public sealed class LocalSurvey
{
	private readonly Dictionary<ISyntaxNode, OpenLocal> _locals = [];
	private readonly Dictionary<LocalVariableSymbol, OpenLocal> _symbols = [];
	private readonly Dictionary<IResolvedExpressionNode, ImmutableArray<TrackedLocal>> _tracked = [];
	private readonly HashSet<IResolvedExpressionNode> _defaulted = [];
	private readonly List<LocalUse> _uses = [];
	
	public bool HasLocals => _locals.Count > 0;
	
	public OpenLocal Declare(OpenLocal local)
	{
		if (_locals.TryGetValue(local.Declaration, out var existing))
			return existing;
		
		_locals[local.Declaration] = local;
		return local;
	}
	
	public void Bind(LocalVariableSymbol symbol, OpenLocal local) => _symbols[symbol] = local;
	
	public OpenLocal? Find(LocalVariableSymbol symbol) => _symbols.GetValueOrDefault(symbol);
	
	public void Track(IResolvedExpressionNode node, IEnumerable<TrackedLocal> locals)
	{
		var all = GetTracked(node).Union(locals).ToImmutableArray();
		if (!all.IsEmpty)
			_tracked[node] = all;
	}
	
	public ImmutableArray<TrackedLocal> GetTracked(IResolvedExpressionNode node) =>
		_tracked.GetValueOrDefault(node, []);
	
	public void MarkDefaulted(IResolvedExpressionNode node) => _defaulted.Add(node);
	
	public bool IsDefaulted(IResolvedExpressionNode node) => _defaulted.Contains(node);
	
	public void Add(LocalUse use) => _uses.Add(use);
	
	public void RemoveLast() => _uses.RemoveAt(_uses.Count - 1);
	
	public LocalSettlement Settle(Func<TypeSymbol, TypeSymbol, bool> converts, Func<TypeSymbol, TypeSymbol, bool> casts)
	{
		var parents = _locals.Values.ToDictionary(static local => local, static local => local);
		foreach (var use in _uses.Where(static use => use.Kind == LocalUseKind.Link))
			parents[FindRoot(parents, use.Local)] = FindRoot(parents, use.Other!);
		
		var types = new Dictionary<ISyntaxNode, TypeSymbol>();
		var conflicts = new List<(OpenLocal, ImmutableArray<TypeSymbol>)>();
		foreach (var group in _locals.Values.GroupBy(local => FindRoot(parents, local)))
		{
			var members = group.ToHashSet();
			var isCounter = members.Any(static local => local.IsCounter);
			var reads = FindTypes(members, LocalUseKind.Read, isCounter);
			var writes = FindTypes(members, LocalUseKind.Write, isCounter);
			var targets = FindTypes(members, LocalUseKind.Cast, false);
			var choice = Choose(reads, writes, type => targets.Where(target => !casts(type, target)).ToList(),
				converts, out var conflicting);
			if (choice is null && conflicting.IsDefault)
				continue;
			
			foreach (var member in group)
			{
				types[member.Declaration] = choice ?? NativeSymbols.Invalid;
				if (!conflicting.IsDefault)
					conflicts.Add((member, conflicting));
			}
		}
		
		return new(types, conflicts);
	}
	
	private List<TypeSymbol> FindTypes(HashSet<OpenLocal> members, LocalUseKind kind, bool isCounter) => _uses
		.Where(use => use.Kind == kind && members.Contains(use.Local))
		.Select(static use => use.Type!)
		.Where(type => !isCounter || type is IntegerType { Kind: not PrimitiveTypeKind.Char })
		.Distinct()
		.ToList();
	
	private static TypeSymbol? Choose(List<TypeSymbol> reads, List<TypeSymbol> writes,
		Func<TypeSymbol, List<TypeSymbol>> findUncastable, Func<TypeSymbol, TypeSymbol, bool> converts,
		out ImmutableArray<TypeSymbol> conflicting)
	{
		conflicting = default;
		if (reads.Count > 0)
		{
			var roots = reads.Where(root => reads.All(other => other == root || converts(root, other))).ToList();
			if (roots.Count > 1)
				roots = roots.Where(root => findUncastable(root).Count == 0).ToList();
			
			if (roots is not [var root])
			{
				conflicting = [..reads];
				return null;
			}
			
			if (findUncastable(root) is [_, ..] uncastable)
			{
				conflicting = [root, ..uncastable];
				return null;
			}
			
			if (writes.FirstOrDefault(write => write != root && !converts(write, root)) is { } stray)
			{
				conflicting = [root, stray];
				return null;
			}
			
			return root;
		}
		
		if (writes.Count == 0)
			return null;
		
		var sinks = writes.Where(sink => writes.All(other => other == sink || converts(other, sink))).ToList();
		if (sinks.Count > 1)
			sinks = sinks.Where(sink => findUncastable(sink).Count == 0).ToList();
		
		if (sinks is not [var sink])
		{
			conflicting = [..writes];
			return null;
		}
		
		if (findUncastable(sink) is [_, ..] stuck)
		{
			conflicting = [sink, ..stuck];
			return null;
		}
		
		return sink;
	}
	
	private static OpenLocal FindRoot(Dictionary<OpenLocal, OpenLocal> parents, OpenLocal local)
	{
		while (parents[local] != local)
			local = parents[local] = parents[parents[local]];
		
		return local;
	}
}
