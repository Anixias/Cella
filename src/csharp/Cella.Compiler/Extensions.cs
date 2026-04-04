namespace Cella.Compiler;

internal static class Extensions
{
	public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
		where TValue : new()
	{
		if (dictionary.TryGetValue(key, out var value))
			return value;
		
		value = new TValue();
		dictionary.Add(key, value);
		return value;
	}
	
	public static IEnumerable<TSource> WhereNot<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
	{
		foreach (var item in source)
			if (!predicate(item))
				yield return item;
	}
}