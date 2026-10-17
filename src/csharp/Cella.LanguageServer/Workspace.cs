using System.Text.Json.Nodes;
using Cella.Compiler;
using Cella.Compiler.Building;
using Cella.Core.Binding;
using Cella.Diagnostics;

namespace Cella.LanguageServer;

public sealed class Workspace(Func<JsonObject, Task> notify, TimeSpan debounce)
{
	private const int ErrorMessage = 1;
	private const int InfoMessage = 3;
	
	private sealed class CollectedOutput : IBuildOutput
	{
		public List<string> Lines { get; } = [];
		
		public void WriteLine(string text) => Lines.Add(text);
		public void WriteError(string text) => Lines.Add(text);
		
		public void Report(IEnumerable<Diagnostic> diagnostics)
		{
		}
	}
	
	private sealed record Checker(Builder Builder, CollectedOutput Output);
	
	private readonly Lock _lock = new();
	private readonly Dictionary<string, (string Uri, string Text)> _documents = new(Protocol.PathComparer);
	private readonly List<string> _roots = [];
	private readonly Dictionary<string, Checker> _checkers = new(Protocol.PathComparer);
	private readonly Dictionary<string, HashSet<string>> _published = new(Protocol.PathComparer);
	private readonly Dictionary<string, BuildResult> _results = new(Protocol.PathComparer);
	private readonly SemaphoreSlim _changes = new(0);
	private readonly CancellationTokenSource _stopping = new();
	private Task? checking;
	
	public void Initialize(JsonObject? parameters)
	{
		if (parameters?["workspaceFolders"] is JsonArray { Count: > 0 } folders)
		{
			foreach (var folder in folders)
				AddRoot(Protocol.ToPath((string)folder!["uri"]!));
		}
		else if ((string?)parameters?["rootUri"] is { } rootUri)
			AddRoot(Protocol.ToPath(rootUri));
		else if ((string?)parameters?["rootPath"] is { } rootPath)
			AddRoot(Path.GetFullPath(rootPath));
		
		checking = Task.Run(CheckContinuouslyAsync);
		RequestCheck();
	}
	
	public void Open(string uri, string text)
	{
		var path = Protocol.ToPath(uri);
		lock (_lock)
			_documents[path] = (uri, text);
		
		if (!IsInRoot(path) && FindProjectDirectory(path) is { } directory)
			AddRoot(directory);
		
		RequestCheck();
	}
	
	public void Change(string uri, string text)
	{
		lock (_lock)
			_documents[Protocol.ToPath(uri)] = (uri, text);
		
		RequestCheck();
	}
	
	public void Close(string uri)
	{
		lock (_lock)
			_documents.Remove(Protocol.ToPath(uri));
		
		RequestCheck();
	}
	
	public void RequestCheck() => _changes.Release();
	
	public SymbolReference? Find(string uri, int line, int character)
	{
		var path = Protocol.ToPath(uri);
		string? text;
		ProjectCheck[] projects;
		lock (_lock)
		{
			text = _documents.TryGetValue(path, out var document) ? document.Text : null;
			projects = [.._results.Values.SelectMany(static result => result.Projects)];
		}
		
		text ??= File.Exists(path) ? File.ReadAllText(path) : null;
		if (text is null)
			return null;
		
		var offset = Protocol.ToOffset(text, line, character);
		return projects
			.Where(project => project.Files.Any(file => Protocol.PathComparer.Equals(file.Symbol.FullPath, path)))
			.Select(project => project.Model?.Value.Find(path, offset))
			.FirstOrDefault(static reference => reference is not null);
	}
	
	public string UriOf(string path)
	{
		lock (_lock)
			return _documents.TryGetValue(path, out var document) ? document.Uri : Protocol.ToUri(path);
	}
	
	public async Task StopAsync()
	{
		await _stopping.CancelAsync();
		if (checking is not null)
			await checking;
	}
	
	private void AddRoot(string path)
	{
		lock (_lock)
		{
			if (_roots.Any(root => Contains(root, path)))
				return;
			
			_roots.RemoveAll(root => Contains(path, root));
			_roots.Add(path);
		}
	}
	
	private bool IsInRoot(string path)
	{
		lock (_lock)
			return _roots.Any(root => Contains(root, path));
	}
	
	private static bool Contains(string root, string path) =>
		Protocol.PathComparer.Equals(root, path) ||
		path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	
	private static string? FindProjectDirectory(string path)
	{
		for (var directory = Path.GetDirectoryName(path);
		     directory is not null;
		     directory = Path.GetDirectoryName(directory))
		{
			if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.celp").Any())
				return directory;
		}
		
		return null;
	}
	
	private async Task CheckContinuouslyAsync()
	{
		var ct = _stopping.Token;
		try
		{
			while (true)
			{
				await _changes.WaitAsync(ct);
				if (debounce > TimeSpan.Zero)
					await Task.Delay(debounce, ct);
				
				while (_changes.Wait(0))
				{
				}
				
				await CheckAllAsync(ct);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}
	
	private async Task CheckAllAsync(CancellationToken ct)
	{
		string[] roots;
		Dictionary<string, string> unsaved;
		Dictionary<string, string> uris;
		lock (_lock)
		{
			roots = [.._roots];
			unsaved = _documents.ToDictionary(static pair => pair.Key, static pair => pair.Value.Text,
				Protocol.PathComparer);
			
			uris = _documents.ToDictionary(static pair => pair.Key, static pair => pair.Value.Uri,
				Protocol.PathComparer);
		}
		
		foreach (var root in roots)
		{
			if (!_checkers.TryGetValue(root, out var checker))
			{
				var output = new CollectedOutput();
				_checkers[root] = checker = new(new Builder(new BuildOptions
				{
					Target = TargetTriple.FromHost().WithDefaultAbi(Builder.ToolchainsDirectory),
					ChecksOnly = true
				}, output), output);
			}
			
			checker.Output.Lines.Clear();
			BuildResult result;
			try
			{
				result = await CompilerThread.Run(() => checker.Builder.BuildAsync(root, unsaved, ct));
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				_checkers.Remove(root);
				await LogAsync(ErrorMessage, $"Checking '{root}' failed: {exception}");
				continue;
			}
			
			foreach (var line in checker.Output.Lines)
				await LogAsync(InfoMessage, line);
			
			lock (_lock)
				_results[root] = result;
			
			await PublishAsync(root, result, uris);
		}
	}
	
	private async Task PublishAsync(string root, BuildResult result, IReadOnlyDictionary<string, string> uris)
	{
		var byFile = new Dictionary<string, JsonArray>(Protocol.PathComparer);
		foreach (var project in result.Projects)
		{
			foreach (var diagnostic in project.Diagnostics)
			{
				var path = diagnostic.SourceLocation.Source.FilePath is { Length: > 0 } file
					? file
					: project.ProjectPath;
				
				if (!byFile.TryGetValue(path, out var diagnostics))
					byFile[path] = diagnostics = [];
				
				diagnostics.Add(Protocol.ToLsp(diagnostic));
			}
		}
		
		var current = new HashSet<string>(byFile.Keys, Protocol.PathComparer);
		foreach (var (path, diagnostics) in byFile)
			await PublishAsync(path, diagnostics, uris);
		
		foreach (var path in _published.GetValueOrDefault(root, []).Where(path => !current.Contains(path)))
			await PublishAsync(path, [], uris);
		
		_published[root] = current;
	}
	
	private Task PublishAsync(string path, JsonArray diagnostics, IReadOnlyDictionary<string, string> uris) =>
		notify(Protocol.Notification("textDocument/publishDiagnostics", new JsonObject
		{
			["uri"] = uris.GetValueOrDefault(path) ?? Protocol.ToUri(path),
			["diagnostics"] = diagnostics
		}));
	
	private Task LogAsync(int type, string message) =>
		notify(Protocol.Notification("window/logMessage", new JsonObject { ["type"] = type, ["message"] = message }));
}