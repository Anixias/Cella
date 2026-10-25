using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Cella.Core.Analysis;

public enum NamingCategory
{
	Types,
	Traits,
	TypeParameters,
	Functions,
	Fields,
	Properties,
	Locals,
	Parameters,
	Constants,
	Globals,
	EnumCases,
	Modules
}

public enum NamingStyle
{
	PascalCase,
	CamelCase,
	SnakeCase,
	ScreamingCase,
	Lowercase
}

public sealed class NamingPattern
{
	private static readonly Dictionary<string, NamingStyle> _styles = new(StringComparer.Ordinal)
	{
		["PascalCase"] = NamingStyle.PascalCase,
		["camelCase"] = NamingStyle.CamelCase,
		["snake_case"] = NamingStyle.SnakeCase,
		["SCREAMING_CASE"] = NamingStyle.ScreamingCase,
		["lowercase"] = NamingStyle.Lowercase
	};
	
	private readonly ImmutableArray<(string Prefix, NamingStyle Style, string Suffix)> _alternatives;
	
	private NamingPattern(string text, ImmutableArray<(string Prefix, NamingStyle Style, string Suffix)> alternatives)
	{
		Text = text;
		_alternatives = alternatives;
	}
	
	public string Text { get; }
	
	public static NamingPattern? Parse(string text)
	{
		var alternatives = new List<(string, NamingStyle, string)>();
		foreach (var alternative in text.Split('|'))
		{
			var open = alternative.IndexOf('{');
			var close = alternative.IndexOf('}');
			if (open < 0 || close < open || alternative.IndexOf('{', open + 1) >= 0 ||
			    alternative.IndexOf('}', close + 1) >= 0 ||
			    !_styles.TryGetValue(alternative[(open + 1)..close], out var style))
				return null;
			
			var (prefix, suffix) = (alternative[..open], alternative[(close + 1)..]);
			if (!prefix.All(IsNameCharacter) || !suffix.All(IsNameCharacter))
				return null;
			
			alternatives.Add((prefix, style, suffix));
		}
		
		return new(text, [..alternatives]);
	}
	
	public bool Matches(string name) => _alternatives.Any(alternative =>
		name.Length > alternative.Prefix.Length + alternative.Suffix.Length &&
		name.StartsWith(alternative.Prefix, StringComparison.Ordinal) &&
		name.EndsWith(alternative.Suffix, StringComparison.Ordinal) &&
		IsStyle(name[alternative.Prefix.Length..^alternative.Suffix.Length], alternative.Style));
	
	public string? Suggest(string name)
	{
		var (prefix, style, suffix) = _alternatives[0];
		var core = name;
		if (core.StartsWith(prefix, StringComparison.Ordinal) && core.Length > prefix.Length)
			core = core[prefix.Length..];
		
		if (core.EndsWith(suffix, StringComparison.Ordinal) && core.Length > suffix.Length)
			core = core[..^suffix.Length];
		
		var words = SplitWords(core);
		if (words.Count == 0)
			return null;
		
		var suggestion = prefix + Format(words, style) + suffix;
		return suggestion != name && Matches(suggestion) && !char.IsDigit(suggestion[0]) ? suggestion : null;
	}
	
	private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
	
	private static bool IsStyle(string text, NamingStyle style)
	{
		var runes = text.EnumerateRunes().ToList();
		return style switch
		{
			NamingStyle.PascalCase => Rune.IsUpper(runes[0]) && IsMixedCase(runes),
			NamingStyle.CamelCase => IsLowerLetter(runes[0]) && IsMixedCase(runes),
			NamingStyle.SnakeCase => IsSeparated(text, IsLowerLetter),
			NamingStyle.ScreamingCase => IsSeparated(text, IsUpperLetter),
			_ => IsLowerLetter(runes[0]) && runes.All(static rune => IsLowerLetter(rune) || IsDigit(rune))
		};
	}
	
	private static bool IsLowerLetter(Rune rune) => Rune.IsLetter(rune) && !Rune.IsUpper(rune);
	
	private static bool IsUpperLetter(Rune rune) => Rune.IsLetter(rune) && !Rune.IsLower(rune);
	
	private static bool IsDigit(Rune rune) =>
		Rune.IsDigit(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber;
	
	private static bool IsMixedCase(List<Rune> runes)
	{
		for (var i = 0; i < runes.Count; i++)
		{
			if (!Rune.IsLetter(runes[i]) && !IsDigit(runes[i]) ||
			    i > 0 && Rune.IsUpper(runes[i]) && Rune.IsUpper(runes[i - 1]) && !FollowsOneLetterWord(runes, i))
				return false;
		}
		
		return true;
	}
	
	private static bool FollowsOneLetterWord(List<Rune> runes, int index) =>
		(index < 2 || !Rune.IsUpper(runes[index - 2])) && index + 1 < runes.Count && IsLowerLetter(runes[index + 1]);
	
	private static bool IsSeparated(string text, Func<Rune, bool> isLetter) =>
		text.Split('_').All(word => word.Length > 0 && word.EnumerateRunes().ToList() is var runes &&
		                            isLetter(runes[0]) && runes.All(rune => isLetter(rune) || IsDigit(rune)));
	
	private static List<string> SplitWords(string text)
	{
		var words = new List<string>();
		foreach (var part in text.Split('_', StringSplitOptions.RemoveEmptyEntries))
		{
			var start = 0;
			for (var i = 1; i < part.Length; i++)
			{
				var startsWord = char.IsUpper(part[i]) &&
				                 (!char.IsUpper(part[i - 1]) || i + 1 < part.Length && char.IsLower(part[i + 1]));
				
				if (!startsWord)
					continue;
				
				words.Add(part[start..i]);
				start = i;
			}
			
			words.Add(part[start..]);
		}
		
		return words;
	}
	
	private static string Format(List<string> words, NamingStyle style) => style switch
	{
		NamingStyle.PascalCase => string.Concat(words.Select(Capitalize)),
		NamingStyle.CamelCase => words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(Capitalize)),
		NamingStyle.SnakeCase => string.Join('_', words.Select(static word => word.ToLowerInvariant())),
		NamingStyle.ScreamingCase => string.Join('_', words.Select(static word => word.ToUpperInvariant())),
		_ => string.Concat(words.Select(static word => word.ToLowerInvariant()))
	};
	
	private static string Capitalize(string word) => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
}

public static class NamingRules
{
	public static IReadOnlyDictionary<NamingCategory, NamingPattern> Defaults { get; } =
		Enum.GetValues<NamingCategory>().ToDictionary(static category => category, static category =>
			NamingPattern.Parse(
				category is NamingCategory.Types or NamingCategory.Traits or NamingCategory.TypeParameters
					? "{PascalCase}"
					: "{camelCase}")!);
	
	public static IReadOnlyDictionary<NamingCategory, NamingPattern> Create(
		IReadOnlyDictionary<string, string>? configured)
	{
		var rules = new Dictionary<NamingCategory, NamingPattern>(Defaults);
		foreach (var (name, text) in configured ?? new Dictionary<string, string>())
		{
			if (Enum.GetNames<NamingCategory>().Contains(name) && NamingPattern.Parse(text) is { } pattern)
				rules[Enum.Parse<NamingCategory>(name)] = pattern;
		}
		
		return rules;
	}
}