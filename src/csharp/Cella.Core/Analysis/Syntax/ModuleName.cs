using System.Collections.Immutable;
using Cella.Core.Analysis.Text;

namespace Cella.Core.Analysis.Syntax;

public readonly struct ModuleName
{
	public static readonly ModuleName Error = new([]);
	
	public readonly ImmutableArray<Token> identifiers;
	public readonly string text;
	
	public ModuleName(IEnumerable<Token> identifiers)
	{
		this.identifiers = identifiers.ToImmutableArray();
		text = string.Join('.', this.identifiers.Select(i => i.Text));
	}
	
	public override string ToString()
	{
		return text;
	}
}