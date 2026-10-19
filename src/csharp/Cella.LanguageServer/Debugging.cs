using System.Text.Json.Nodes;
using Cella.Core.Binding;
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
}