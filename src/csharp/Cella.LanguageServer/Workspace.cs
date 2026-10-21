using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cella.Compiler;
using Cella.Compiler.Building;
using Cella.Compiler.Projects;
using Cella.Core.Binding;
using Cella.Core.Binding.Nodes;
using Cella.Core.CodeGen;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
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
	
	private sealed record Checker(Builder Builder, CollectedOutput Output, BuildOptions Options);
	
	private readonly Lock _lock = new();
	
	private readonly Dictionary<string, (string Uri, string Text, int? Version)>
		_documents = new(Protocol.PathComparer);
	
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
	private bool hasChecked;
	private bool refreshesTokens;
	private int refreshCount;
	
	private BuildOptions buildOptions = new()
	{
		Target = TargetTriple.FromHost().WithDefaultAbi(),
		ChecksOnly = true
	};
	
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
		
		Configure(parameters?["initializationOptions"]?["configuration"] as JsonObject);
		checking = Task.Run(CheckContinuouslyAsync);
		RequestCheck();
	}
	
	public void Open(string uri, string text, int? version)
	{
		var path = Protocol.ToPath(uri);
		lock (_lock)
			_documents[path] = (uri, text, version);
		
		if (!IsInRoot(path) && FindProjectDirectory(path) is { } directory)
			AddRoot(directory);
		
		RequestCheck();
	}
	
	public void Change(string uri, string text, int? version)
	{
		lock (_lock)
			_documents[Protocol.ToPath(uri)] = (uri, text, version);
		
		RequestCheck();
	}
	
	public void Close(string uri)
	{
		lock (_lock)
			_documents.Remove(Protocol.ToPath(uri));
		
		RequestCheck();
	}
	
	public void RequestCheck() => _changes.Release();
	
	public void Configure(JsonObject? configuration)
	{
		var target = TargetTriple.FromHost();
		if ((string?)configuration?["target"] is { Length: > 0 } name)
		{
			if (TargetTriple.Parse(name) is { } parsed)
				target = parsed;
			else
				_ = LogAsync(ErrorMessage, $"Unknown target '{name}'");
		}
		
		var flags = new SortedDictionary<string, bool>(StringComparer.Ordinal);
		if (configuration?["flags"] is JsonObject declared)
		{
			foreach (var (flag, value) in declared)
			{
				if (value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
					flags[flag] = (bool)value;
			}
		}
		
		var options = new BuildOptions
		{
			Target = target.WithDefaultAbi(),
			OptimizeMode = (bool?)configuration?["release"] == true ? OptimizeMode.Release : OptimizeMode.Debug,
			FlagOverrides = flags,
			ChecksOnly = true
		};
		
		lock (_lock)
		{
			if (options.Target == buildOptions.Target && options.OptimizeMode == buildOptions.OptimizeMode &&
			    flags.Count == buildOptions.FlagOverrides.Count && flags.All(flag =>
				    buildOptions.FlagOverrides.TryGetValue(flag.Key, out var value) && value == flag.Value))
				return;
			
			buildOptions = options;
		}
		
		RequestCheck();
	}
	
	public async Task<SymbolReference?> FindAsync(string uri, int line, int character, CancellationToken ct) =>
		(await FindInModelAsync(uri, line, character, ct))?.Reference;
	
	public async Task<(SymbolReference Reference, SemanticModel Model)?> FindInModelAsync(string uri, int line,
		int character, CancellationToken ct)
	{
		var path = Protocol.ToPath(uri);
		var (projects, text) = await GetCheckedProjectsAsync(path, ct);
		text ??= File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
		if (text is null)
			return null;
		
		var offset = Protocol.ToOffset(text, line, character);
		foreach (var model in ModelsOf(path, projects))
		{
			if (model.Find(path, offset) is { } reference)
				return (reference, model);
		}
		
		return null;
	}
	
	public async Task<(IReadOnlyList<SymbolReference> References, ImmutableArray<Token> Keywords)> GetTokensAsync(
		string uri, CancellationToken ct)
	{
		var path = Protocol.ToPath(uri);
		var projects = (await GetCheckedProjectsAsync(path, ct)).Projects;
		var references = ModelsOf(path, projects)
			.Select(model => model.GetReferences(path))
			.FirstOrDefault(static references => references.Count > 0) ?? [];
		
		return (references, FindFile(projects, path)?.Syntax is FileNode file ? file.ContextualKeywords : []);
	}
	
	public async Task<(IReadOnlyList<Diagnostic> Diagnostics, ResolvedFileNode? File, string? Text)> GetFileAsync(
		string uri, CancellationToken ct)
	{
		var path = Protocol.ToPath(uri);
		var (projects, text) = await GetCheckedProjectsAsync(path, ct);
		var diagnostics = projects
			.SelectMany(static project => project.Diagnostics)
			.Where(diagnostic => Protocol.PathComparer.Equals(diagnostic.SourceLocation.Source.FilePath, path))
			.ToList();
		
		return (diagnostics, FindFile(projects, path), text);
	}
	
	public async Task<IReadOnlyList<(FunctionInfo Function, SourceLocation Name, bool IsMain)>> GetRunnablesAsync(
		string uri, CancellationToken ct)
	{
		var path = Protocol.ToPath(uri);
		if (FindFile((await GetCheckedProjectsAsync(path, ct)).Projects, path) is not { } file)
			return [];
		
		return
		[
			..file.Declarations
				.OfType<ResolvedFunctionNode>()
				.Where(static node => node.Syntax is FunctionNode)
				.Select(static node => (Function: node.FunctionInfo, Name: ((FunctionNode)node.Syntax).Identifier,
					IsMain: node.FunctionInfo.Symbol.Name == "main" &&
					        SignatureCollector.CanRunAsMain(node.FunctionInfo)))
				.Where(static runnable => runnable.IsMain || SignatureCollector.CanRunAsEntry(runnable.Function))
				.Select(static runnable => (runnable.Function, runnable.Name.SourceLocation, runnable.IsMain))
		];
	}
	
	private async Task<(ProjectCheck[] Projects, string? Text)> GetCheckedProjectsAsync(string path,
		CancellationToken ct)
	{
		while (true)
		{
			Task completed;
			ProjectCheck[] projects;
			string? text;
			bool isCurrent;
			lock (_lock)
			{
				completed = checkCompleted.Task;
				projects = [.._results.Values.SelectMany(static result => result.Projects)];
				text = _documents.TryGetValue(path, out var document) ? document.Text : null;
				isCurrent = hasChecked && (text is null ||
				                           checkedTexts.TryGetValue(path, out var checkedText) && checkedText == text);
			}
			
			if (!isCurrent)
			{
				_urgent.Release();
				using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, ct);
				await completed.WaitAsync(stopping.Token);
				continue;
			}
			
			return (projects, text);
		}
	}
	
	public async Task<SymbolReference?> FindRenameableAsync(string uri, int line, int character,
		CancellationToken ct)
	{
		if (await FindAsync(uri, line, character, ct) is not { Declaration: { } declaration } reference ||
		    reference.Symbol is ModulePathSymbol || !IsName(reference.Location.GetText().ToString()) ||
		    !IsInRoot(declaration.Source.FilePath))
			return null;
		
		return reference;
	}
	
	public static bool IsName(string text) =>
		text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') &&
		text.All(static c => char.IsLetterOrDigit(c) || c == '_') &&
		TokenTypeInfo.GetKeyword(text) is not { IsContextual: false };
	
	public async Task<IReadOnlyList<SourceLocation>> FindUsagesAsync(string uri, int line, int character,
		bool includeDeclaration, CancellationToken ct)
	{
		if ((await FindAsync(uri, line, character, ct))?.Declaration is not { } declaration)
			return [];
		
		ProjectCheck[] projects;
		lock (_lock)
			projects = [.._results.Values.SelectMany(static result => result.Projects)];
		
		var seen = new Dictionary<string, HashSet<TextRange>>(Protocol.PathComparer);
		var usages = new List<SourceLocation>();
		foreach (var model in projects.Select(static project => project.Model?.Value).OfType<SemanticModel>())
		{
			foreach (var reference in model.GetUsages(declaration))
			{
				if (!includeDeclaration && IsSameLocation(reference.Location, declaration))
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
	
	private static ResolvedFileNode? FindFile(IEnumerable<ProjectCheck> projects, string path) => projects
		.SelectMany(static project => project.Files)
		.FirstOrDefault(file => Protocol.PathComparer.Equals(file.Symbol.FullPath, path));
	
	private static IEnumerable<SemanticModel> ModelsOf(string path, IEnumerable<ProjectCheck> projects) => projects
		.Where(project => CellaProject.IsProjectFile(path) ||
		                  project.Files.Any(file => Protocol.PathComparer.Equals(file.Symbol.FullPath, path)))
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
		Dictionary<string, (string Uri, int? Version)> opened;
		lock (_lock)
		{
			roots = [.._roots];
			unsaved = _documents.ToDictionary(static pair => pair.Key, static pair => pair.Value.Text,
				Protocol.PathComparer);
			
			opened = _documents.ToDictionary(static pair => pair.Key,
				static pair => (pair.Value.Uri, pair.Value.Version), Protocol.PathComparer);
		}
		
		foreach (var root in roots)
		{
			if (await RunAsync(root, builder => builder.BuildAsync(root, unsaved, ct), ct) is not { } result)
				continue;
			
			lock (_lock)
				_results[root] = result;
			
			await PublishAsync(root, result, opened);
		}
		
		TaskCompletionSource completed;
		lock (_lock)
		{
			checkedTexts = unsaved;
			hasChecked = true;
			completed = checkCompleted;
			checkCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}
		
		completed.SetResult();
		if (refreshesTokens)
			await notify(Protocol.Request($"refresh-{++refreshCount}", "workspace/semanticTokens/refresh"));
	}
	
	public async Task<CompletionProbe?> ProbeAsync(string path, string text, int offset, CancellationToken ct)
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
		using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, ct);
		return await RunAsync(root, builder => builder.CompleteAsync(root, unsaved, probe, _stopping.Token),
			stopping.Token) is null
			? null
			: probe;
	}
	
	private async Task<BuildResult?> RunAsync(string root, Func<Builder, Task<BuildResult>> build,
		CancellationToken ct)
	{
		await _building.WaitAsync(ct);
		try
		{
			BuildOptions options;
			lock (_lock)
				options = buildOptions;
			
			if (!_checkers.TryGetValue(root, out var checker) || checker.Options != options)
			{
				var output = new CollectedOutput();
				_checkers[root] = checker = new(new Builder(options, output), output, options);
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
	
	public string? GetText(string uri) => TextAt(Protocol.ToPath(uri));
	
	public JsonArray ListProjects()
	{
		string[] roots;
		lock (_lock)
			roots = [.._roots];
		
		return ProjectRequests.List(roots, TextAt);
	}
	
	private string? TextAt(string path)
	{
		lock (_lock)
		{
			if (_documents.TryGetValue(path, out var document))
				return document.Text;
		}
		
		return File.Exists(path) ? File.ReadAllText(path) : null;
	}
	
	private async Task PublishAsync(string root, BuildResult result,
		IReadOnlyDictionary<string, (string Uri, int? Version)> opened)
	{
		var byFile = new Dictionary<string, JsonArray>(Protocol.PathComparer);
		foreach (var problem in result.ProjectDiagnostics)
		{
			foreach (var path in problem.Projects)
				DiagnosticsAt(path).Add(Protocol.ToLsp(problem, TextAt(path)));
		}
		
		foreach (var project in result.Projects)
		{
			foreach (var diagnostic in project.Diagnostics)
			{
				var diagnostics = DiagnosticsAt(diagnostic.SourceLocation.Source.FilePath is { Length: > 0 } file
					? file
					: project.ProjectPath);
				
				diagnostics.Add(Protocol.ToLsp(diagnostic));
				if (Protocol.ToUnnecessaryLsp(diagnostic) is { } unnecessary)
					diagnostics.Add(unnecessary);
			}
			
			foreach (var inactive in project.InactiveCode.Concat(project.Model?.Value.InactiveCalls ?? []))
				DiagnosticsAt(inactive.Source.FilePath).Add(Protocol.ToInactiveLsp(inactive));
		}
		
		var current = new HashSet<string>(byFile.Keys, Protocol.PathComparer);
		foreach (var (path, diagnostics) in byFile)
			await PublishAsync(path, diagnostics, opened);
		
		foreach (var path in _published.GetValueOrDefault(root, []).Where(path => !current.Contains(path)))
			await PublishAsync(path, [], opened);
		
		_published[root] = current;
		
		JsonArray DiagnosticsAt(string path)
		{
			if (!byFile.TryGetValue(path, out var diagnostics))
				byFile[path] = diagnostics = [];
			
			return diagnostics;
		}
	}
	
	private Task PublishAsync(string path, JsonArray diagnostics,
		IReadOnlyDictionary<string, (string Uri, int? Version)> opened)
	{
		var parameters = new JsonObject { ["uri"] = Protocol.ToUri(path), ["diagnostics"] = diagnostics };
		if (opened.TryGetValue(path, out var document))
		{
			parameters["uri"] = document.Uri;
			if (document.Version is { } version)
				parameters["version"] = version;
		}
		
		return notify(Protocol.Notification("textDocument/publishDiagnostics", parameters));
	}
	
	private Task LogAsync(int type, string message) =>
		notify(Protocol.Notification("window/logMessage", new JsonObject { ["type"] = type, ["message"] = message }));
}