namespace Cella.Core.Collections;

public sealed class StateSnapshot
{
	private readonly List<Action> _restores = [];
	private readonly List<object> _collections = [];
	
	internal IReadOnlyList<object> Collections => _collections;
	
	public void Restore()
	{
		foreach (var restore in _restores)
			restore();
	}
	
	internal void Save<T>(List<T> list)
	{
		var saved = list.ToArray();
		Track(list, () =>
		{
			list.Clear();
			list.AddRange(saved);
		});
	}
	
	internal void Save<T>(HashSet<T> set)
	{
		var saved = set.ToArray();
		Track(set, () =>
		{
			set.Clear();
			set.UnionWith(saved);
		});
	}
	
	internal void Save<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull
	{
		var saved = dictionary.ToArray();
		Track(dictionary, () =>
		{
			dictionary.Clear();
			foreach (var (key, value) in saved)
				dictionary[key] = value;
		});
	}
	
	internal void SaveNested<TKey, TItem>(Dictionary<TKey, List<TItem>> dictionary) where TKey : notnull
	{
		var saved = dictionary.Select(static pair => (pair.Key, Items: pair.Value.ToArray())).ToArray();
		Track(dictionary, () =>
		{
			dictionary.Clear();
			foreach (var (key, items) in saved)
				dictionary[key] = [..items];
		});
	}
	
	internal void SaveNested<TKey, TInnerKey, TInnerValue>(
		Dictionary<TKey, OrderedDictionary<TInnerKey, TInnerValue>> dictionary)
		where TKey : notnull
		where TInnerKey : notnull
	{
		var saved = dictionary.Select(static pair => (pair.Key, Items: pair.Value.ToArray())).ToArray();
		Track(dictionary, () =>
		{
			dictionary.Clear();
			foreach (var (key, items) in saved)
				dictionary[key] = new OrderedDictionary<TInnerKey, TInnerValue>(items);
		});
	}
	
	internal void SaveValue<T>(Func<T> get, Action<T> set)
	{
		var saved = get();
		_restores.Add(() => set(saved));
	}
	
	private void Track(object collection, Action restore)
	{
		_collections.Add(collection);
		_restores.Add(restore);
	}
}