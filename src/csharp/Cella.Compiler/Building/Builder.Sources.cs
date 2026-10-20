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
	private async Task<(ImmutableArray<FileNode> Files, ImmutableArray<ImportExpression> Imports, DiagnosticList
		Diagnostics, ImmutableArray<SourceLocation> InactiveCode)> ProcessProject(
		ProjectInfo project, ImmutableArray<ProjectInfo> references, IReadOnlyDictionary<string, bool> flags,
		CancellationToken ct = default)
	{
		var files = new List<FileNode>();
		var inactiveCode = new List<SourceLocation>();
		var diagnostics = new DiagnosticList();
		var projectImports = new List<ImportExpression>();
		foreach (var reference in references)
			projectImports.AddRange(await ParseImportList(reference, nameof(CellaProject.Prelude), flags, diagnostics,
				ct));
		
		projectImports.AddRange(await ParseImportList(project, nameof(CellaProject.Imports), flags, diagnostics, ct));
		
		foreach (var path in CellaProject.FindSourceFiles(project.Directory).Order(StringComparer.Ordinal))
		{
			var (ast, fileDiagnostics) = await ParseFile(project, flags, path, ct);
			diagnostics.AddRange(fileDiagnostics);
			inactiveCode.AddRange(ast?.InactiveCode ?? []);
			if (ast is { IsExcluded: false })
				files.Add(ast);
		}
		
		return ([..files], [..projectImports], diagnostics, [..inactiveCode]);
	}
	
	private async Task<List<ImportExpression>> ParseImportList(ProjectInfo project, string setting,
		IReadOnlyDictionary<string, bool> flags, DiagnosticList diagnostics, CancellationToken ct)
	{
		var entries = project.Project.ImportEntries.Where(entry => entry.Setting == setting).ToList();
		if (entries.Count == 0)
			return [];
		
		var source = new StringSource(await ReadTextAsync(project.FilePath, ct), project.FilePath);
		var imports = new List<ImportExpression>();
		foreach (var entry in entries)
		{
			var span = new SourceLocation(source, new TextRange(entry.Start, entry.End));
			ImmutableArray<Token> tokens = entry.IsExact
				? ScanRange(source, entry.Start, entry.End)
				:
				[
					..new FilteredScanner(new StringSource(entry.Value, project.FilePath))
						.Select(token => token with { SourceLocation = span })
				];
			
			var parser = new FileParser(tokens, Path.GetFileName(project.FilePath), project.FilePath, flags);
			if (parser.ParseImportEntry() is { } import)
				imports.Add(import with { IsPrelude = setting == nameof(CellaProject.Prelude) });
			
			diagnostics.AddRange(parser.Diagnostics);
		}
		
		return imports;
	}
	
	private static ImmutableArray<Token> ScanRange(StringSource source, int start, int end)
	{
		var scanner = new Scanner(source);
		var tokens = ImmutableArray.CreateBuilder<Token>();
		for (var position = start; position < end;)
		{
			var (token, next) = scanner.ScanToken(position);
			if (token.Type == TokenType.EndOfFile || next <= position)
				break;
			
			if (!token.Type.IsFiltered)
				tokens.Add(token);
			
			position = next;
		}
		
		tokens.Add(Token.EndOfFile(source, end));
		return tokens.ToImmutable();
	}
	
	private async Task<(FileNode? Ast, List<Diagnostic> Diagnostics)> ParseFile(ProjectInfo project,
		IReadOnlyDictionary<string, bool> flags, string sourcePath, CancellationToken ct)
	{
		// TODO Create some kind of StreamSource so the entire file doesn't have to be loaded into memory
		var bytes = await ReadBytesAsync(sourcePath, ct);
		ct.ThrowIfCancellationRequested();
		
		using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
		var source = new StringSource(await reader.ReadToEndAsync(ct), sourcePath)
		{
			Name = SourcePaths.Relative(project.Directory, sourcePath)
		};
		
		var diagnostics = new List<Diagnostic>();
		if (FindInvalidUtf8(bytes) is { } offset)
			diagnostics.Add(new(DiagnosticSeverity.Error, new SourceLocation(source, new TextRange(offset, offset + 1)),
				"Invalid UTF-8"));
		
		// TODO Change parser to retrieve tokens as needed
		var tokens = new FilteredScanner(source).ToImmutableArray();
		ct.ThrowIfCancellationRequested();
		
		var parser = new FileParser(tokens, source.Name, sourcePath, flags) { Probe = probe };
		var ast = parser.Parse();
		diagnostics.AddRange(parser.Diagnostics);
		return (ast, diagnostics);
	}
	
	private async Task<byte[]> ReadBytesAsync(string path, CancellationToken ct) =>
		openFiles.TryGetValue(Path.GetFullPath(path), out var text)
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