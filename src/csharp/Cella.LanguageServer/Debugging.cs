using System.Text.Json.Nodes;
using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.LanguageServer;

public static class Debugging
{
	public static int FindStatementStart(string text, int offset)
	{
		var start = offset;
		while (start < text.Length && text[start] is ' ' or '\t')
			start++;
		
		return start;
	}
	
	public static IEnumerable<JsonObject> DescribeGlobals(IEnumerable<CompletionCandidate> candidates) => candidates
		.Select(static candidate => candidate.Global)
		.OfType<GlobalInfo>()
		.Select(Describe);
	
	public static JsonObject Describe(GlobalInfo global) => new()
	{
		["name"] = global.Symbol.Name,
		["debugName"] = string.Join("::", [..global.DebugScope, global.Symbol.Name])
	};
	
	public static bool IsDeclaredAt(GlobalInfo global, SourceLocation declaration)
	{
		var identifier = global.Symbol.Syntax.Identifier.SourceLocation;
		return identifier.Range.Start == declaration.Range.Start &&
		       Protocol.PathComparer.Equals(identifier.Source.FilePath, declaration.Source.FilePath);
	}
	
	public static SymbolReference? FindValue(IReadOnlyList<SymbolReference> references, int offset) => references
		.Where(reference => reference.Location.Range.Start <= offset && offset < reference.Location.Range.End)
		.MinBy(static reference => reference.Location.Range.Length) is { Symbol: var symbol } found && IsValue(symbol)
		? found
		: null;
	
	public static SourceLocation ExtendValue(IReadOnlyList<SymbolReference> references, SymbolReference value,
		string text)
	{
		var (source, range) = value.Location;
		var start = range.Start;
		while (start > 0 && text[start - 1] == '.' && references.FirstOrDefault(owner =>
			       owner.Location.Range.End == start - 1 && IsValue(owner.Symbol) &&
			       Protocol.PathComparer.Equals(owner.Location.Source.FilePath, source.FilePath)) is { } owner)
			start = owner.Location.Range.Start;
		
		return new SourceLocation(source, range with { Start = start });
	}
	
	private static bool IsValue(Symbol symbol) =>
		symbol is LocalVariableSymbol or ParameterSymbol or FieldSymbol or PropertySymbol or GlobalSymbol;
}