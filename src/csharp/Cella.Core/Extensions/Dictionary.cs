namespace Cella.Core.Extensions;

public static class DictionaryExtensions
{
	extension<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull
	{
		public void Add(KeyValuePair<TKey, TValue> pair) => dictionary.Add(pair.Key, pair.Value);
	}
}