using System.Collections;
using System.Collections.Immutable;

namespace Cella.Core.Collections;

public class OrderedSet<T> : ISet<T>, IReadOnlySet<T>, IList<T>, IReadOnlyList<T>
{
	public int Count => _list.Count;
	
	bool ICollection<T>.IsReadOnly => false;
	
	private readonly List<T> _list;
	private readonly HashSet<T> _set;
	
	public OrderedSet()
	{
		_list = [];
		_set = [];
	}
	
	public OrderedSet(int capacity)
	{
		_list = new(capacity);
		_set = new(capacity);
	}
	
	public OrderedSet(IEnumerable<T> collection)
	{
		ArgumentNullException.ThrowIfNull(collection);
		
		if (collection is ICollection<T> { Count: > 0 and var count })
		{
			_list = new(count);
			_set = new(count);
		}
		else
		{
			_list = [];
			_set = [];
		}
		
		foreach (var item in collection)
			if (_set.Add(item))
				_list.Add(item);
	}
	
	public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	
	void ICollection<T>.Add(T item) => Add(item);
	
	public bool Contains(T item) => _set.Contains(item);
	public bool IsProperSubsetOf(IEnumerable<T> other) => _set.IsProperSubsetOf(other);
	public bool IsProperSupersetOf(IEnumerable<T> other) => _set.IsProperSupersetOf(other);
	public bool IsSubsetOf(IEnumerable<T> other) => _set.IsSubsetOf(other);
	public bool IsSupersetOf(IEnumerable<T> other) => _set.IsSupersetOf(other);
	public bool Overlaps(IEnumerable<T> other) => _set.Overlaps(other);
	public bool SetEquals(IEnumerable<T> other) => _set.SetEquals(other);
	
	public void ExceptWith(IEnumerable<T> other)
	{
		foreach (var item in other)
			Remove(item);
	}
	
	public void IntersectWith(IEnumerable<T> other)
	{
		var otherSet = other.ToImmutableHashSet();
		for (var i = _list.Count - 1; i >= 0; i--)
		{
			var item = _list[i];
			if (otherSet.Contains(item))
				continue;
			
			_list.RemoveAt(i);
			_set.Remove(item);
		}
	}
	
	public void SymmetricExceptWith(IEnumerable<T> other)
	{
		foreach (var item in other)
			if (Contains(item))
				Remove(item);
	}
	
	public void UnionWith(IEnumerable<T> other)
	{
		foreach (var item in other)
			Add(item);
	}
	
	public bool Add(T item)
	{
		if (!_set.Add(item))
			return false;
		
		_list.Add(item);
		return true;
	}
	
	public void Clear()
	{
		_list.Clear();
		_set.Clear();
	}
	
	public void CopyTo(T[] array, int arrayIndex) => _list.CopyTo(array, arrayIndex);
	
	public bool Remove(T item)
	{
		if (!_set.Remove(item))
			return false;
		
		_list.Remove(item);
		return true;
	}
	
	public int IndexOf(T item) => _list.IndexOf(item);
	
	public void Insert(int index, T item)
	{
		if (_set.Add(item))
			_list.Insert(index, item);
	}
	
	public void RemoveAt(int index)
	{
		var item = _list[index];
		_list.RemoveAt(index);
		_set.Remove(item);
	}
	
	public T this[int index]
	{
		get => _list[index];
		set => _list[index] = value;
	}
}