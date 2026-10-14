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
	
	internal IEnumerable<MovePath> ClearChildren()
	{
		var removed = _children.SelectMany(static child => child.WithDescendants()).ToList();
		_children.Clear();
		return removed;
	}
	
	private IEnumerable<MovePath> WithDescendants() => _children.SelectMany(static child => child.WithDescendants())
		.Prepend(this);
	
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
	private readonly TypePool _typePool;
	private readonly Dictionary<VariableSymbol, MovePath> _roots = [];
	private readonly List<MovePath> _paths = [];
	private readonly HashSet<MovePath> _elementArrays = [];
	
	public int Count => _paths.Count;
	
	private MovePaths(TypePool typePool)
	{
		_typePool = typePool;
	}
	
	public static MovePaths Build(LoweredFunction function, IEnumerable<MemoryEvent> events, IEnumerable<Place> places,
		TypePool typePool, bool tracksElements = false)
	{
		var paths = new MovePaths(typePool);
		var parameters = function.Info.Symbol.Parameters;
		for (var i = 0; i < parameters.Length; i++)
			paths.AddRoot(parameters[i], function.Info.Signature.GetDeclaredType(i));
		
		var eventList = events.ToList();
		foreach (var define in eventList.OfType<DefineEvent>())
			paths.AddRoot(define.Local, define.Local.Type);
		
		if (tracksElements)
			paths.MarkElementArrays(eventList);
		
		foreach (var memoryEvent in eventList)
		{
			var place = memoryEvent switch
			{
				WriteEvent e => e.Place,
				AccessEvent { Kind: AccessKind.Move } e => e.Place,
				AccessEvent { Kind: AccessKind.Address } e => e.Place.AddressedStorage,
				DropEvent e => e.Place,
				_ => null
			};
			
			if (place is not null)
				paths.Track(place);
		}
		
		foreach (var place in places)
			paths.Track(place);
		
		foreach (var root in paths._roots.Values)
			root.Number(paths._paths);
		
		return paths;
	}
	
	public MovePath GetRoot(VariableSymbol symbol) => _roots[symbol];
	
	public bool TracksElements(MovePath path) => _elementArrays.Contains(path);
	
	public IReadOnlyCollection<MovePath> ElementArrays => _elementArrays;
	
	private void MarkElementArrays(List<MemoryEvent> events)
	{
		foreach (var memoryEvent in events)
		{
			var place = memoryEvent switch
			{
				WriteEvent e => e.Place,
				AccessEvent e => e.Place,
				DropEvent e => e.Place,
				_ => null
			};
			
			if (place is not null)
				MarkElementArray(place);
		}
		
		foreach (var array in _elementArrays.ToList())
			_elementArrays.ExceptWith(array.ClearChildren());
	}
	
	private void MarkElementArray(Place place)
	{
		if (!_roots.TryGetValue(place.Root, out var root))
			return;
		
		var type = root.Type;
		for (var depth = 0; depth < place.Path.Length; depth++)
		{
			if (place.Path[depth] is IndexProjection { Index: null })
			{
				if (ShapeLayout.GetLength(type) is not null && Track(place with { Path = place.Path[..depth] }) is
					    { } array)
					_elementArrays.Add(array);
				
				return;
			}
			
			if (GetPartType(type, place.Path[depth]) is not { } part)
				return;
			
			type = part;
		}
	}
	
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
	
	public IEnumerable<MovePath> FindAncestors(Place place)
	{
		if (!_roots.TryGetValue(place.Root, out var path))
			yield break;
		
		foreach (var projection in place.Path)
		{
			yield return path;
			if (path.GetChild(projection) is not { } child)
				yield break;
			
			path = child;
		}
	}
	
	private void AddRoot(VariableSymbol symbol, TypeSymbol type)
	{
		if (!_roots.ContainsKey(symbol))
			_roots.Add(symbol, new(null, type, CountParts(type)));
	}
	
	private MovePath? Track(Place place)
	{
		if (!_roots.TryGetValue(place.Root, out var path))
			return null;
		
		foreach (var projection in place.Path)
		{
			if (_elementArrays.Contains(path))
				return null;
			
			if (path.GetChild(projection) is { } child)
			{
				path = child;
				continue;
			}
			
			if (GetPartType(path.Type, projection) is not { } type)
				return null;
			
			path = path.AddChild(projection, type, CountParts(type));
		}
		
		return path;
	}
	
	private TypeSymbol? GetPartType(TypeSymbol type, Projection projection) => (type, projection) switch
	{
		(RecordSymbol { IsUnion: false }, FieldProjection p) => _typePool.GetTypeOfMember(p.Field),
		(ArrayType a, IndexProjection { Index: { } index }) when index >= 0 &&
		                                                         (index < a.Length || a.LengthParameter is not null) =>
			a.ElementType,
		_ => null
	};
	
	private int CountParts(TypeSymbol type) => type switch
	{
		RecordSymbol { IsUnion: false } r => _typePool.GetMembers(r).OfType<FieldSymbol>().Count(),
		ArrayType a when a.Length.Sign >= 0 && a.Length <= int.MaxValue => (int)a.Length,
		_ => 0
	};
}