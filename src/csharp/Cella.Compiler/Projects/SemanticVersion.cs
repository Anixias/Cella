using System.Reflection;

namespace Cella.Compiler.Projects;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease)
	: IComparable<SemanticVersion>
{
	public static SemanticVersion Current { get; } = Parse(typeof(SemanticVersion).Assembly
		.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion)!.Value;
	
	public static SemanticVersion? Parse(string text)
	{
		var dash = text.IndexOf('-');
		var core = (dash < 0 ? text : text[..dash]).Split('.');
		var prerelease = dash < 0 ? null : text[(dash + 1)..];
		if (core.Length != 3 || !core.All(IsNumber) ||
		    prerelease is not null && !prerelease.Split('.').All(IsIdentifier))
			return null;
		
		return new SemanticVersion(int.Parse(core[0]), int.Parse(core[1]), int.Parse(core[2]), prerelease);
	}
	
	private static bool IsNumber(string part) => part.Length is > 0 and <= 9 && part.All(char.IsAsciiDigit) &&
	                                             (part.Length == 1 || part[0] != '0');
	
	private static bool IsIdentifier(string part) => part.Length > 0 &&
	                                                 part.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
	                                                 (!part.All(char.IsAsciiDigit) || IsNumber(part));
	
	public int CompareTo(SemanticVersion other)
	{
		var result = Major.CompareTo(other.Major);
		if (result == 0)
			result = Minor.CompareTo(other.Minor);
		
		if (result == 0)
			result = Patch.CompareTo(other.Patch);
		
		return result != 0
			? result
			: (Prerelease, other.Prerelease) switch
			{
				(null, null) => 0,
				(null, _) => 1,
				(_, null) => -1,
				var (mine, theirs) => ComparePrerelease(mine.Split('.'), theirs.Split('.'))
			};
	}
	
	private static int ComparePrerelease(string[] mine, string[] theirs)
	{
		for (var i = 0; i < mine.Length && i < theirs.Length; i++)
		{
			var result = (mine[i].All(char.IsAsciiDigit), theirs[i].All(char.IsAsciiDigit)) switch
			{
				(true, true) => int.Parse(mine[i]).CompareTo(int.Parse(theirs[i])),
				(true, false) => -1,
				(false, true) => 1,
				_ => string.CompareOrdinal(mine[i], theirs[i])
			};
			
			if (result != 0)
				return result;
		}
		
		return mine.Length.CompareTo(theirs.Length);
	}
	
	public override string ToString() =>
		Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}