using System.Text.Json.Nodes;
using Cella.Compiler;
using Cella.Compiler.Building;
using Cella.Core.Binding;
using Cella.Core.Text;
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
	private readonly SemaphoreSlim _urgent = new(0);
	private readonly SemaphoreSlim _building = new(1, 1);
	private readonly CancellationTokenSource _stopping = new();
	private Task? checking;
	private Dictionary<string, string> checkedTexts = new(Protocol.PathComparer);
	private TaskCompletionSource checkCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool refreshesTokens;
	private int refreshCount;
	
	public void Initialize(JsonObject? parameters)
	{
		refreshesTokens = (bool?)parameters?["capabilities"]?["workspace"]?["semanticTokens"]?["refreshSupport"] ==
		                  true;
		
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
		return ModelsOf(path, projects)
			.Select(model => model.Find(path, offset))
			.FirstOrDefault(static reference => reference is not null);
	}
	
	public async Task<IReadOnlyList<SymbolReference>> GetReferencesAsync(string uri)
	{
		var path = Protocol.ToPath(uri);
		while (true)
		{
			Task completed;
			ProjectCheck[] projects;
			bool isCurrent;
			lock (_lock)
			{
				completed = checkCompleted.Task;
				projects = [.._results.Values.SelectMany(static result => result.Projects)];
				isCurrent = !_documents.TryGetValue(path, out var document) ||
				            checkedTexts.TryGetValue(path, out var checkedText) && checkedText == document.Text;
			}
			
			if (!isCurrent)
			{
				_urgent.Release();
				await completed.WaitAsync(_stopping.Token);
				continue;
			}
			
			return ModelsOf(path, projects)
				.Select(model => model.GetReferences(path))
				.FirstOrDefault(static references => references.Count > 0) ?? [];
		}
	}
	
	public IReadOnlyList<SourceLocation> FindUsages(string uri, int line, int character, bool includeDeclaration)
	{
		if (Find(uri, line, character)?.Declaration is not { } declaration)
			return [];
		
		ProjectCheck[] projects;
		lock (_lock)
			projects = [.._results.Values.SelectMany(static result => result.Projects)];
		
		var seen = new Dictionary<string, HashSet<TextRange>>(Protocol.PathComparer);
		var usages = new List<SourceLocation>();
		foreach (var model in projects.Select(static project => project.Model?.Value).OfType<SemanticModel>())
		{
			foreach (var reference in model.GetReferences())
			{
				if (reference.Declaration is not { } target || !IsSameLocation(target, declaration) ||
				    !includeDeclaration && IsSameLocation(reference.Location, declaration))
					continue;
				
				var path = reference.Location.Source.FilePath;
				if (!seen.TryGetValue(path, out var ranges))
					seen[path] = ranges = [];
				
				if (ranges.Add(reference.Location.Range))
					usages.Add(reference.Location);
			}
		}
		
		return
		[
			..usages.OrderBy(static usage => usage.Source.FilePath, Protocol.PathComparer)
				.ThenBy(static usage => usage.Range.Start)
		];
	}
	
	private static bool IsSameLocation(SourceLocation left, SourceLocation right) =>
		left.Range == right.Range && Protocol.PathComparer.Equals(left.Source.FilePath, right.Source.FilePath);
	
	private static IEnumerable<SemanticModel> ModelsOf(string path, IEnumerable<ProjectCheck> projects) => projects
		.Where(project => project.Files.Any(file => Protocol.PathComparer.Equals(file.Symbol.FullPath, path)))
		.Select(static project => project.Model?.Value)
		.OfType<SemanticModel>();
	
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
				{
					using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
					await Task.WhenAny(Task.Delay(debounce, waiting.Token), _urgent.WaitAsync(waiting.Token));
					await waiting.CancelAsync();
				}
				
				while (_changes.Wait(0) || _urgent.Wait(0))
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
			if (await RunAsync(root, builder => builder.BuildAsync(root, unsaved, ct)) is not { } result)
				continue;
			
			lock (_lock)
				_results[root] = result;
			
			await PublishAsync(root, result, uris);
		}
		
		TaskCompletionSource completed;
		lock (_lock)
		{
			checkedTexts = unsaved;
			completed = checkCompleted;
			checkCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}
		
		completed.SetResult();
		if (refreshesTokens)
			await notify(Protocol.Request($"refresh-{++refreshCount}", "workspace/semanticTokens/refresh"));
	}
	
	public async Task<CompletionProbe?> ProbeAsync(string path, string text, int offset)
	{
		string? root;
		Dictionary<string, string> unsaved;
		lock (_lock)
		{
			root = _roots.FirstOrDefault(root => Contains(root, path));
			unsaved = _documents.ToDictionary(static pair => pair.Key, static pair => pair.Value.Text,
				Protocol.PathComparer);
		}
		
		if (root is null)
			return null;
		
		unsaved[path] = text;
		var probe = new CompletionProbe(path, offset);
		return await RunAsync(root, builder => builder.CompleteAsync(root, unsaved, probe, _stopping.Token)) is null
			? null
			: probe;
	}
	
	private async Task<BuildResult?> RunAsync(string root, Func<Builder, Task<BuildResult>> build)
	{
		await _building.WaitAsync(_stopping.Token);
		try
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
				result = await CompilerThread.Run(() => build(checker.Builder));
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				_checkers.Remove(root);
				await LogAsync(ErrorMessage, $"Checking '{root}' failed: {exception}");
				return null;
			}
			
			foreach (var line in checker.Output.Lines)
				await LogAsync(InfoMessage, line);
			
			return result;
		}
		finally
		{
			_building.Release();
		}
	}
	
	public string? GetText(string uri)
	{
		var path = Protocol.ToPath(uri);
		lock (_lock)
		{
			if (_documents.TryGetValue(path, out var document))
				return document.Text;
		}
		
		return File.Exists(path) ? File.ReadAllText(path) : null;
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
				if (Protocol.ToUnnecessaryLsp(diagnostic) is { } unnecessary)
					diagnostics.Add(unnecessary);
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