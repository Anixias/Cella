namespace Cella.Compiler;

internal static class DictionaryExtensions
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
}