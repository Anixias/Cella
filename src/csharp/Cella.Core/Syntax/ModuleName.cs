using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public readonly struct ModuleName : IEquatable<ModuleName>
{
	public SourceLocation SourceLocation { get; } = SourceLocation.None;
	public string Text { get; } = string.Empty;
	
	public ModuleName(IEnumerable<Token> parts)
	{
		var array = parts.ToImmutableArray();
		SourceLocation = GetSourceLocation(array);
		Text = string.Join('.', array.Select(static p => p.GetText()));
	}
	
	private static SourceLocation GetSourceLocation(ImmutableArray<Token> parts)
	{
		if (parts.Length == 0)
			return SourceLocation.None;
		
		var (source, range) = parts[0].SourceLocation;
		for (var i = 1; i < parts.Length; i++)
			range = range.Join(parts[i].SourceLocation.Range);
		
		return new(source, range);
	}
	
	public override string ToString() => Text;
	public bool Equals(ModuleName other) => Text == other.Text;
	public override bool Equals(object? obj) => obj is ModuleName other && Equals(other);
	public override int GetHashCode() => Text.GetHashCode();
	public static bool operator ==(ModuleName left, ModuleName right) => left.Equals(right);
	public static bool operator !=(ModuleName left, ModuleName right) => !left.Equals(right);
}