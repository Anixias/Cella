using System.Text.Json;
using System.Text.Json.Nodes;
using Cella.Compiler.Building;
using Cella.Core.Binding;
using Cella.Core.Binding.Nodes;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.LanguageServer;

public sealed record TypeMove(JsonObject Action, ISource Source, int Offset, TextRange Removal);

public static class TypeMoves
{
	public const string Kind = "refactor.move";
	
	public static TypeMove? Find(ProjectCheck project, ResolvedFileNode file, TextRange requested,
		Func<string, string> uriOf)
	{
		if (file.Syntax is not FileNode syntax || project.Model?.Value is not { } model)
			return null;
		
		var source = syntax.SourceLocation.Source;
		foreach (var declaration in syntax.Declarations)
		{
			if (TypeOf(declaration) is not var (identifier, extent, visibility) ||
			    syntax.DeclarationsWithSyntaxErrors.Contains(declaration) ||
			    syntax.BrokenDeclarations.Contains(declaration))
				continue;
			
			var start = identifier.SourceLocation.Range.Start;
			if (!Touches(new(start, LineEnd(source, start)), requested))
				continue;
			
			var target = Path.Combine(Path.GetDirectoryName(syntax.FullPath) ?? "",
				JsonNamingPolicy.CamelCase.ConvertName(identifier.Text) + ".ce");
			
			if (Protocol.PathComparer.Equals(target, syntax.FullPath))
				return null;
			
			var targetFile = project.Files.FirstOrDefault(candidate =>
				Protocol.PathComparer.Equals(candidate.Symbol.FullPath, target))?.Syntax as FileNode;
			
			if (targetFile is null ? File.Exists(target) : targetFile.ModuleName != syntax.ModuleName)
				return null;
			
			var references = model.GetReferences(syntax.FullPath);
			if (!CanMove(syntax, declaration, identifier, extent, visibility, references))
				return null;
			
			var imports = FindImports(model, file, extent, references);
			var (action, removal) = Move(syntax, extent, target, targetFile, imports, uriOf);
			return new(action, source, start, removal);
		}
		
		return null;
	}
	
	private static (Token Identifier, SourceLocation Extent, Token? Visibility)? TypeOf(IDeclarationNode declaration) =>
		declaration switch
		{
			RecordNode record => (record.Identifier, record.Extent, record.Visibility),
			EnumNode enumNode => (enumNode.Identifier, enumNode.Extent, enumNode.Visibility),
			TraitNode trait => (trait.Identifier, trait.Extent, trait.Visibility),
			_ => null
		};
	
	private static (Token Identifier, Token? Visibility)? NameOf(IDeclarationNode declaration) => declaration switch
	{
		FunctionNode function => (function.Identifier, function.Visibility),
		ExternalFunctionNode external => (external.Identifier, external.Visibility),
		GlobalNode global => (global.Identifier, global.Visibility),
		PropertyNode property => (property.Identifier, property.Visibility),
		_ => TypeOf(declaration) is var (identifier, _, visibility) ? (identifier, visibility) : null
	};
	
	private static bool CanMove(FileNode syntax, IDeclarationNode declaration, Token identifier, SourceLocation extent,
		Token? visibility, IReadOnlyList<SymbolReference> references)
	{
		var fileOnly = syntax.Declarations
			.Where(other => other != declaration)
			.Select(NameOf)
			.Where(static name => name?.Visibility?.Text == "pvt")
			.Select(static name => name!.Value.Identifier.SourceLocation.Range)
			.ToHashSet();
		
		foreach (var reference in references)
		{
			if (reference.Declaration is not { } declared ||
			    !Protocol.PathComparer.Equals(declared.Source.FilePath, syntax.FullPath))
				continue;
			
			var isInside = Contains(extent.Range, reference.Location.Range.Start);
			if (isInside && fileOnly.Contains(declared.Range))
				return false;
			
			if (!isInside && visibility?.Text == "pvt" && declared.Range == identifier.SourceLocation.Range)
				return false;
		}
		
		return true;
	}
	
	private static SortedDictionary<string, SortedSet<string>> FindImports(SemanticModel model, ResolvedFileNode file,
		SourceLocation extent, IEnumerable<SymbolReference> references)
	{
		var imports = new SortedDictionary<string, SortedSet<string>>(ImportEdits.Comparer);
		foreach (var reference in references.Where(reference => Contains(extent.Range, reference.Location.Range.Start)))
		{
			foreach (var use in model.FindImportSources(file.Symbol, reference.Symbol))
			{
				var import = use.Import;
				if (import.IsPrelude ||
				    !Protocol.PathComparer.Equals(import.SourceLocation.Source.FilePath, file.Symbol.FullPath))
					continue;
				
				IEnumerable<string> names = use.Name is { } name
					? [name.Text]
					: import.Import switch
					{
						TokenImport single => [single.Token.Text],
						ListImport list => list.Tokens.Select(static token => token.Text),
						_ => [ImportEdits.Wildcard]
					};
				
				var module = import.ModuleName.Text;
				if (!imports.TryGetValue(module, out var listed))
					imports[module] = listed = new(ImportEdits.Comparer);
				
				listed.UnionWith(names);
			}
		}
		
		foreach (var listed in imports.Values.Where(static listed => listed.Contains(ImportEdits.Wildcard)))
			listed.IntersectWith([ImportEdits.Wildcard]);
		
		return imports;
	}
	
	public static void AddSourceEdits(TypeMove move, IEnumerable<TextEdit> edits)
	{
		var changes = (JsonArray)move.Action["edit"]!["documentChanges"]!;
		var sourceEdits = (JsonArray)changes[^1]!["edits"]!;
		var index = 0;
		foreach (var edit in edits)
			sourceEdits.Insert(index++, new JsonObject
			{
				["range"] = Protocol.ToRange(edit.Location),
				["newText"] = edit.NewText
			});
	}
	
	private static (JsonObject Action, TextRange Removal) Move(FileNode syntax, SourceLocation extent, string target,
		FileNode? targetFile, SortedDictionary<string, SortedSet<string>> imports, Func<string, string> uriOf)
	{
		var source = syntax.SourceLocation.Source;
		var newline = source.FindNewline();
		var start = IncludeLeadingComments(source, LineStart(source, extent.Range.Start));
		var end = LineEnd(source, extent.Range.End);
		var text = source.GetText(new(start, end)).ToString().TrimEnd();
		var targetUri = uriOf(target);
		var changes = new JsonArray();
		if (targetFile is null)
		{
			changes.Add(new JsonObject
			{
				["kind"] = "create",
				["uri"] = targetUri,
				["options"] = new JsonObject { ["ignoreIfExists"] = true }
			});
			
			var lines = new List<string>();
			if (syntax.ModuleName.Text.Length > 0)
				lines.AddRange([$"mod {syntax.ModuleName.Text}", ""]);
			
			if (imports.Count > 0)
				lines.AddRange([
					..imports.OrderBy(static import => import.Key, Comparer<string>.Create(ImportEdits.CompareModules))
						.Select(static import => ImportLine(import.Key, import.Value)),
					""
				]);
			
			lines.Add(text);
			changes.Add(DocumentEdit(targetUri, [
				new JsonObject
				{
					["range"] = new JsonObject { ["start"] = Origin(), ["end"] = Origin() },
					["newText"] = string.Join(newline, lines) + newline
				}
			]));
		}
		else
		{
			var targetSource = targetFile.SourceLocation.Source;
			var targetLength = targetSource.Length;
			var separator = targetLength > 0 && targetSource[targetLength - 1] == '\n' ? "" : newline;
			var edits = MergeInsertions(AddImports(targetFile, imports))
				.Append(new(new(targetSource, new(targetLength, targetLength)), separator + newline + text + newline));
			
			changes.Add(DocumentEdit(targetUri, edits.Select(static edit => new JsonObject
			{
				["range"] = Protocol.ToRange(edit.Location),
				["newText"] = edit.NewText
			})));
		}
		
		var removal = Removal(source, start, end);
		changes.Add(DocumentEdit(uriOf(syntax.FullPath), [
			new JsonObject
			{
				["range"] = Protocol.ToRange(new(source, removal)),
				["newText"] = ""
			}
		]));
		
		return (new JsonObject
		{
			["title"] = $"Move to '{Path.GetFileName(target)}'",
			["kind"] = Kind,
			["edit"] = new JsonObject { ["documentChanges"] = changes }
		}, removal);
		
		static JsonObject Origin() => new() { ["line"] = 0, ["character"] = 0 };
	}
	
	private static IEnumerable<TextEdit> AddImports(FileNode file, SortedDictionary<string, SortedSet<string>> imports)
	{
		foreach (var (module, names) in imports)
		{
			var existing = file.Imports.Where(import => !import.IsPrelude && import.ModuleName.Text == module).ToList();
			if (existing.Any(static import => import.Import is FullImport))
				continue;
			
			var present = existing.SelectMany(static import => import.Import switch
			{
				TokenImport single => [single.Token.Text],
				ListImport list => list.Tokens.Select(static token => token.Text),
				_ => []
			}).ToHashSet();
			
			var missing = names.Where(name => !present.Contains(name)).ToList();
			if (missing.Count > 0)
				foreach (var edit in ImportEdits.Create(file, module, missing))
					yield return edit;
		}
	}
	
	private static IEnumerable<TextEdit> MergeInsertions(IEnumerable<TextEdit> edits)
	{
		foreach (var group in edits.GroupBy(static edit => edit.Location.Range))
		{
			var location = group.First().Location;
			var newline = location.Source.FindNewline();
			yield return new(location, string.Concat(group.Select((edit, index) =>
				index > 0 && edit.NewText.StartsWith(newline + newline, StringComparison.Ordinal)
					? edit.NewText[newline.Length..]
					: edit.NewText)));
		}
	}
	
	private static string ImportLine(string module, SortedSet<string> names) =>
		$"use {module}.{(names.Count == 1 ? names.First() : $"[{string.Join(", ", names)}]")}";
	
	private static JsonObject DocumentEdit(string uri, IEnumerable<JsonObject> edits) => new()
	{
		["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = null },
		["edits"] = new JsonArray([..edits])
	};
	
	private static TextRange Removal(ISource source, int start, int end)
	{
		var after = SkipBlankLines(source, end);
		if (after >= source.Length && start > 0)
		{
			var before = start;
			while (before > 0 && IsBlankLine(source, LineStart(source, before - 1)))
				before = LineStart(source, before - 1);
			
			return new(before, source.Length);
		}
		
		return start == 0 || IsBlankLine(source, LineStart(source, start - 1)) ? new(start, after) : new(start, end);
	}
	
	private static int IncludeLeadingComments(ISource source, int start)
	{
		while (start > 0)
		{
			var previous = LineStart(source, start - 1);
			if (!source.GetText(new(previous, start)).ToString().TrimStart().StartsWith("//", StringComparison.Ordinal))
				break;
			
			start = previous;
		}
		
		return start;
	}
	
	private static int SkipBlankLines(ISource source, int position)
	{
		while (position < source.Length && IsBlankLine(source, position))
			position = LineEnd(source, position);
		
		return position;
	}
	
	private static bool IsBlankLine(ISource source, int lineStart) =>
		source.GetText(new(lineStart, LineEnd(source, lineStart))).IsWhiteSpace();
	
	private static int LineStart(ISource source, int position)
	{
		while (position > 0 && source[position - 1] != '\n')
			position--;
		
		return position;
	}
	
	private static int LineEnd(ISource source, int position)
	{
		while (position < source.Length && source[position] != '\n')
			position++;
		
		return Math.Min(position + 1, source.Length);
	}
	
	private static bool Contains(TextRange range, int position) => range.Start <= position && position < range.End;
	
	private static bool Touches(TextRange range, TextRange requested) =>
		range.Start <= requested.End && requested.Start <= range.End;
}