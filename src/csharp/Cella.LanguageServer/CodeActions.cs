using System.Text.Json.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.LanguageServer;

public static class CodeActions
{
	public const string QuickFix = "quickfix";
	public const string Rewrite = "refactor.rewrite";
	public const string RenameCommand = "cella.rename";
	
	public static IEnumerable<JsonObject> FromDiagnostics(IEnumerable<Diagnostic> diagnostics, TextRange requested,
		Func<string, string> uriOf)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var diagnostic in diagnostics)
		{
			if (diagnostic.Fixes.IsEmpty || !Touches(diagnostic, requested))
				continue;
			
			foreach (var fix in diagnostic.Fixes)
			{
				var edit = ToWorkspaceEdit(fix.Edits, uriOf);
				if (!seen.Add(fix.Title + edit.ToJsonString()))
					continue;
				
				yield return new JsonObject
				{
					["title"] = fix.Title,
					["kind"] = QuickFix,
					["diagnostics"] = new JsonArray(Protocol.ToLsp(diagnostic)),
					["edit"] = edit
				};
			}
		}
	}
	
	public static JsonObject ToAction(DiagnosticFix fix, string kind, Func<string, string> uriOf,
		bool startsRenames = false)
	{
		var action = new JsonObject
		{
			["title"] = fix.Title,
			["kind"] = kind,
			["edit"] = ToWorkspaceEdit(fix.Edits, uriOf)
		};
		
		if (startsRenames && fix.NewNameEnd is { } end && fix.Edits is [var edit, ..])
			action["command"] = new JsonObject
			{
				["title"] = "Rename",
				["command"] = RenameCommand,
				["arguments"] = new JsonArray(uriOf(edit.Location.Source.FilePath), ToPositionAfter(edit, end))
			};
		
		return action;
	}
	
	private static JsonObject ToPositionAfter(TextEdit edit, int end)
	{
		var (line, character) = Protocol.ToLineCharacter(edit.Location.Source, edit.Location.Range.Start);
		var inserted = edit.NewText[..end];
		var lastBreak = inserted.LastIndexOf('\n');
		return new JsonObject
		{
			["line"] = line + inserted.Count(static c => c == '\n'),
			["character"] = lastBreak < 0 ? character + end : end - lastBreak - 1
		};
	}
	
	public static JsonObject ToWorkspaceEdit(IEnumerable<TextEdit> edits, Func<string, string> uriOf)
	{
		var changes = new JsonObject();
		foreach (var group in edits.GroupBy(static edit => edit.Location.Source.FilePath, Protocol.PathComparer))
		{
			changes[uriOf(group.Key)] = new JsonArray([
				..group.OrderBy(static edit => edit.Location.Range.Start).Select(JsonNode (edit) => new JsonObject
				{
					["range"] = Protocol.ToRange(edit.Location),
					["newText"] = edit.NewText
				})
			]);
		}
		
		return new JsonObject { ["changes"] = changes };
	}
	
	public static bool Touches(Diagnostic diagnostic, TextRange requested) =>
		Touches(diagnostic.SourceLocation.Range, requested) ||
		diagnostic.UnnecessaryLocation is { } unnecessary && Touches(unnecessary.Range, requested);
	
	private static bool Touches(TextRange range, TextRange requested) =>
		range.Start <= requested.End && requested.Start <= range.End;
}