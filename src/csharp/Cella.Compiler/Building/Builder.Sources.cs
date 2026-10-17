using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Cella.Compiler.Projects;
using Cella.Core.Syntax;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Compiler.Building;

public sealed partial class Builder
{
	private async Task<(ImmutableArray<FileNode> Files, DiagnosticList Diagnostics)> ProcessProject(
		ProjectInfo project, IReadOnlyDictionary<string, bool> flags, CancellationToken ct = default)
	{
		var files = new List<FileNode>();
		var diagnostics = new DiagnosticList();
		var projectImports = await ParseProjectImports(project, flags, diagnostics, ct);
		foreach (var path in CellaProject.FindSourceFiles(project.Directory).Order(StringComparer.Ordinal))
		{
			var (ast, fileDiagnostics) = await ParseFile(project, flags, path, ct);
			diagnostics.AddRange(fileDiagnostics);
			if (ast is { IsExcluded: false })
				files.Add(projectImports.IsEmpty
					? ast
					: new FileNode(ast.SourceLocation)
					{
						FileName = ast.FileName,
						FullPath = ast.FullPath,
						ModuleName = ast.ModuleName,
						Declarations = ast.Declarations,
						Imports = [..projectImports, ..ast.Imports],
						DeclarationExtents = ast.DeclarationExtents
					});
		}
		
		return ([..files], diagnostics);
	}
	
	private async Task<ImmutableArray<ImportExpression>> ParseProjectImports(ProjectInfo project,
		IReadOnlyDictionary<string, bool> flags, DiagnosticList diagnostics, CancellationToken ct)
	{
		if (project.Project.Imports is not { Count: > 0 } entries)
			return [];
		
		var text = await ReadTextAsync(project.FilePath, ct);
		var ranges = FindImportEntries(text);
		var isLocated = ranges.Count == entries.Count;
		var imports = new List<ImportExpression>();
		for (var i = 0; i < entries.Count; i++)
		{
			var (start, length) = isLocated ? ranges[i] : (0, entries[i].Length);
			var entryText = isLocated ? text : entries[i];
			var display = new StringSource(entryText, project.FilePath);
			var blanked = string.Create(entryText.Length, (entryText, start, length), static (span, state) =>
			{
				for (var c = 0; c < span.Length; c++)
					span[c] = c >= state.start && c < state.start + state.length || state.entryText[c] == '\n'
						? state.entryText[c]
						: ' ';
			});
			
			var source = new StringSource(blanked, project.FilePath);
			var keyword = new Token(TokenType.KeywordUse, new SourceLocation(display, new TextRange(start, start)),
				"use");
			
			ImmutableArray<Token> tokens =
			[
				keyword,
				..new FilteredScanner(source).Select(token =>
					token with { SourceLocation = token.SourceLocation with { Source = display } })
			];
			
			var parser = new FileParser(tokens, Path.GetFileName(project.FilePath), project.FilePath, flags);
			if (parser.ParseImport() is { } import)
				imports.Add(import);
			
			diagnostics.AddRange(parser.Diagnostics);
		}
		
		return [..imports];
	}
	
	private static List<(int Start, int Length)> FindImportEntries(string text)
	{
		var entries = new List<(int Start, int Length)>();
		if (FindImportsList(text) is not { } open)
			return entries;
		
		for (var i = open; i < text.Length && text[i] != ']'; i++)
		{
			if (text[i] == '#')
			{
				while (i < text.Length && text[i] != '\n')
					i++;
				
				continue;
			}
			
			if (text[i] is not ('"' or '\''))
				continue;
			
			var quote = text[i];
			var start = i + 1;
			for (i = start; i < text.Length && text[i] != quote; i++)
			{
				if (quote == '"' && text[i] == '\\')
					i++;
			}
			
			entries.Add((start, i - start));
		}
		
		return entries;
	}
	
	private static int? FindImportsList(string text)
	{
		var offset = 0;
		foreach (var line in text.Split('\n'))
		{
			var trimmed = line.TrimStart();
			if (trimmed.StartsWith("Imports", StringComparison.Ordinal) &&
			    trimmed["Imports".Length..].TrimStart().StartsWith('=') && line.IndexOf('[') is >= 0 and var bracket)
				return offset + bracket + 1;
			
			offset += line.Length + 1;
		}
		
		return null;
	}
	
	private async Task<(FileNode? Ast, List<Diagnostic> Diagnostics)> ParseFile(ProjectInfo project,
		IReadOnlyDictionary<string, bool> flags, string sourcePath, CancellationToken ct)
	{
		// TODO Create some kind of StreamSource so the entire file doesn't have to be loaded into memory
		var bytes = await ReadBytesAsync(sourcePath, ct);
		ct.ThrowIfCancellationRequested();
		
		using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
		var source = new StringSource(await reader.ReadToEndAsync(ct), sourcePath);
		var diagnostics = new List<Diagnostic>();
		if (FindInvalidUtf8(bytes) is { } offset)
			diagnostics.Add(new(DiagnosticSeverity.Error, new SourceLocation(source, new TextRange(offset, offset + 1)),
				"Invalid UTF-8"));
		
		// TODO Change parser to retrieve tokens as needed
		var tokens = new FilteredScanner(source).ToImmutableArray();
		ct.ThrowIfCancellationRequested();
		
		var parser = new FileParser(tokens, Path.GetRelativePath(project.Directory, sourcePath), sourcePath, flags);
		var ast = parser.Parse();
		diagnostics.AddRange(parser.Diagnostics);
		return (ast, diagnostics);
	}
	
	private async Task<byte[]> ReadBytesAsync(string path, CancellationToken ct) =>
		options.OpenFiles.TryGetValue(Path.GetFullPath(path), out var text)
			? Encoding.UTF8.GetBytes(text)
			: await File.ReadAllBytesAsync(path, ct);
	
	private async Task<string> ReadTextAsync(string path, CancellationToken ct)
	{
		var bytes = await ReadBytesAsync(path, ct);
		return Encoding.UTF8.GetString(bytes.AsSpan(bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0));
	}
	
	private async Task<Stream> OpenProjectFileAsync(string path, CancellationToken ct) =>
		new MemoryStream(await ReadBytesAsync(path, ct));
	
	private static int? FindInvalidUtf8(byte[] bytes)
	{
		if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
			return null;
		
		var start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
		for (var index = start; index < bytes.Length;)
		{
			if (Rune.DecodeFromUtf8(bytes.AsSpan(index), out _, out var length) != OperationStatus.Done)
				return Encoding.UTF8.GetCharCount(bytes, start, index - start);
			
			index += length;
		}
		
		return null;
	}
}
