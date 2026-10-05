using Cella.Core.Binding;
using Cella.Core.Lowering;
using Cella.Core.Symbols;

namespace Cella.Core.Analysis;

public sealed class MovePath(Projection? projection, TypeSymbol type, int partCount)
{
	private readonly List<MovePath> _children = [];
	
	public Projection? Projection { get; } = projection;
	public TypeSymbol Type { get; } = type;
	public IReadOnlyList<MovePath> Children => _children;
	public bool IsComplete => partCount > 0 && _children.Count == partCount;
	public int Index { get; private set; }
	public int End { get; private set; }
	
	public MovePath? GetChild(Projection projection) => _children.Find(child => child.Projection == projection);
	
	internal MovePath AddChild(Projection projection, TypeSymbol type, int childPartCount)
	{
		var child = new MovePath(projection, type, childPartCount);
		_children.Add(child);
		return child;
	}
	
	internal void Number(List<MovePath> paths)
	{
		Index = paths.Count;
		paths.Add(this);
		foreach (var child in _children)
			child.Number(paths);
		
		End = paths.Count;
	}
}

public sealed class MovePaths
{
	private const int MaxTrackedElements = 64;
	
	private readonly TypePool _typePool;
	private readonly Dictionary<VariableSymbol, MovePath> _roots = [];
	private readonly List<MovePath> _paths = [];
	
	public int Count => _paths.Count;
	
	private MovePaths(TypePool typePool)
	{
		_typePool = typePool;
	}
	
	public static MovePaths Build(LoweredFunction function, IEnumerable<MemoryEvent> events, TypePool typePool)
	{
		var paths = new MovePaths(typePool);
		var parameters = function.Info.Symbol.Parameters;
		for (var i = 0; i < parameters.Length; i++)
			paths.AddRoot(parameters[i], function.Info.Signature.GetDeclaredType(i));
		
		var eventList = events.ToList();
		foreach (var define in eventList.OfType<DefineEvent>())
			paths.AddRoot(define.Local, define.Local.Type);
		
		foreach (var memoryEvent in eventList)
		{
			var place = memoryEvent switch
			{
				WriteEvent e => e.Place,
				AccessEvent { Kind: AccessKind.Move } e => e.Place,
				DropEvent e => e.Place,
				_ => null
			};
			
			if (place is not null)
				paths.Track(place);
		}
		
		foreach (var root in paths._roots.Values)
			root.Number(paths._paths);
		
		return paths;
	}
	
	public MovePath GetRoot(VariableSymbol symbol) => _roots[symbol];
	
	public MovePath? Find(Place place) =>
		FindPrefix(place) is ({ } path, var depth) && depth == place.Path.Length ? path : null;
	
	public (MovePath? Path, int Depth) FindPrefix(Place place)
	{
		if (!_roots.TryGetValue(place.Root, out var path))
			return (null, 0);
		
		var depth = 0;
		while (depth < place.Path.Length && path.GetChild(place.Path[depth]) is { } child)
		{
			path = child;
			depth++;
		}
		
		return (path, depth);
	}
	
	private void AddRoot(VariableSymbol symbol, TypeSymbol type)
	{
		if (!_roots.ContainsKey(symbol))
			_roots.Add(symbol, new(null, type, CountParts(type)));
	}
	
	private void Track(Place place)
	{
		if (!_roots.TryGetValue(place.Root, out var path))
			return;
		
		foreach (var projection in place.Path)
		{
			if (path.GetChild(projection) is { } child)
			{
				path = child;
				continue;
			}
			
			if (GetPartType(path.Type, projection) is not { } type)
				return;
			
			path = path.AddChild(projection, type, CountParts(type));
		}
	}
	
	private TypeSymbol? GetPartType(TypeSymbol type, Projection projection) => (type, projection) switch
	{
		(RecordSymbol, FieldProjection p) => _typePool.GetTypeOfMember(p.Field),
		(ArrayType a, IndexProjection { Index: { } index }) when IsTracked(a) && index >= 0 && index < a.Length =>
			a.ElementType,
		_ => null
	};
	
	private int CountParts(TypeSymbol type) => type switch
	{
		RecordSymbol r => _typePool.GetMembers(r).OfType<FieldSymbol>().Count(),
		ArrayType a when IsTracked(a) => (int)a.Length,
		_ => 0
	};
	
	private static bool IsTracked(ArrayType type) => type.Length.Sign >= 0 && type.Length <= MaxTrackedElements;
}