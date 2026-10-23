using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Cella.Compiler.Building;
using Cella.Compiler.Projects;
using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.LanguageServer;

public sealed class Server
{
	private const int MethodNotFound = -32601;
	private const int InternalError = -32603;
	private const int RequestCancelled = -32800;
	private const int RequestFailed = -32803;
	private const int FullSync = 1;
	private const int FileCreated = 1;
	private const int FileDeleted = 3;
	
	private readonly MessageConnection _connection;
	private readonly Workspace _workspace;
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();
	private bool hoversPlainText;
	private bool resolvesActions;
	
	public Server(Stream input, Stream output, TimeSpan debounce)
	{
		_connection = new MessageConnection(input, output);
		_workspace = new Workspace(message => _connection.WriteAsync(message, CancellationToken.None), debounce);
	}
	
	public async Task<int> RunAsync(CancellationToken ct = default)
	{
		var isShutDown = false;
		while (await _connection.ReadAsync(ct) is { } message)
		{
			if ((string?)message["method"] is not { } method)
				continue;
			
			var id = message["id"]?.DeepClone();
			if (method == "exit")
			{
				await _workspace.StopAsync();
				return isShutDown ? 0 : 1;
			}
			
			try
			{
				if (method == "shutdown")
				{
					isShutDown = true;
					await _workspace.StopAsync();
				}
				
				if (method == "$/cancelRequest")
				{
					if (message["params"]?["id"] is { } cancelled &&
					    _pending.TryGetValue(cancelled.ToJsonString(), out var pending))
						await pending.CancelAsync();
					
					continue;
				}
				
				if (id is not null && AnswerLater(method, message["params"] as JsonObject) is { } answer)
				{
					var key = id.ToJsonString();
					var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
					_pending[key] = cancellation;
					_ = Task.Run(() => AnswerLaterAsync(id, key, answer, cancellation), CancellationToken.None);
					continue;
				}
				
				var (isHandled, result) = Handle(method, message["params"] as JsonObject);
				if (id is not null)
					await _connection.WriteAsync(isHandled
						? Protocol.Response(id, result)
						: Protocol.ErrorResponse(id, MethodNotFound, $"Unknown method '{method}'"), ct);
			}
			catch (Exception exception)
			{
				await _connection.WriteAsync(id is not null
					? Protocol.ErrorResponse(id, InternalError, exception.Message)
					: Protocol.Notification("window/logMessage", new JsonObject
					{
						["type"] = 1,
						["message"] = $"'{method}' failed: {exception}"
					}), ct);
			}
		}
		
		await _workspace.StopAsync();
		return 1;
	}
	
	private (bool IsHandled, JsonNode? Result) Handle(string method, JsonObject? parameters)
	{
		switch (method)
		{
			case "initialize":
				hoversPlainText =
					(string?)parameters?["capabilities"]?["textDocument"]?["hover"]?["contentFormat"]?[0] ==
					"plaintext";
				
				resolvesActions =
					parameters?["capabilities"]?["textDocument"]?["codeAction"]?["resolveSupport"]?["properties"] is
						JsonArray properties && properties.Any(static property => (string?)property == "edit");
				
				_workspace.Initialize(parameters);
				return (true, Capabilities());
			
			case "textDocument/didOpen":
				_workspace.Open(GetUri(parameters), (string)parameters!["textDocument"]!["text"]!,
					(int?)parameters["textDocument"]!["version"]);
				
				return (true, null);
			
			case "textDocument/didChange":
				var changes = (JsonArray)parameters!["contentChanges"]!;
				_workspace.Change(GetUri(parameters), (string)changes[^1]!["text"]!,
					(int?)parameters["textDocument"]!["version"]);
				
				return (true, null);
			
			case "textDocument/didClose":
				_workspace.Close(GetUri(parameters));
				return (true, null);
			
			case "textDocument/didSave":
				_workspace.RequestCheck();
				return (true, null);
			
			case "workspace/didChangeWatchedFiles":
				if (parameters?["changes"] is JsonArray watched &&
				    watched.Any(static change => ChangesProjects(change)))
					_workspace.ForgetProjects();
				
				_workspace.RequestCheck();
				return (true, null);
			
			case "cella/configuration":
				_workspace.Configure(parameters);
				return (true, null);
			
			case "initialized" or "shutdown":
				return (true, null);
			
			case "cella/projects":
				return (true, _workspace.ListProjects());
			
			case "cella/projectSettings":
				return (true,
					ProjectRequests.Describe(Protocol.ToPath(GetUri(parameters)), GetProjectText(parameters)));
			
			case "cella/editProject":
				return (true, new JsonObject
				{
					["edits"] = ProjectRequests.Edit(Protocol.ToPath(GetUri(parameters)), GetProjectText(parameters),
						(JsonArray)parameters!["edits"]!)
				});
			
			default:
				return (method.StartsWith("$/", StringComparison.Ordinal), null);
		}
	}
	
	private Func<CancellationToken, Task<JsonNode?>>? AnswerLater(string method, JsonObject? parameters) =>
		method switch
		{
			"textDocument/semanticTokens/full" => async ct =>
			{
				var uri = GetUri(parameters);
				var (references, keywords) = await _workspace.GetTokensAsync(uri, ct);
				if (CellaProject.IsProjectFile(Protocol.ToPath(uri)))
					return _workspace.GetText(uri) is { } project
						? SemanticTokens.EncodeProject(project, SettingKeys.Find(project), references)
						: null;
				
				return SemanticTokens.Encode(references, keywords);
			},
			"textDocument/hover" => ct => HoverAsync(parameters, ct),
			"cella/describe" => ct => DescribeAsync(parameters, ct),
			"textDocument/definition" => ct => FindDefinitionAsync(parameters, ct),
			"textDocument/references" => ct => FindReferencesAsync(parameters, ct),
			"textDocument/completion" => ct => CompleteAsync(parameters, ct),
			"textDocument/signatureHelp" => ct => HelpSignatureAsync(parameters, ct),
			"textDocument/codeAction" => ct => FindCodeActionsAsync(parameters, ct),
			"codeAction/resolve" => ct => ResolveCodeActionAsync(parameters, ct),
			"textDocument/prepareRename" => ct => PrepareRenameAsync(parameters, ct),
			"textDocument/rename" => ct => RenameAsync(parameters, ct),
			"cella/visibleGlobals" => ct => FindVisibleGlobalsAsync(parameters, ct),
			"cella/valueAt" => ct => FindValueAsync(parameters, ct),
			"cella/runnables" => async ct => new JsonArray([
				..(await _workspace.GetRunnablesAsync(GetUri(parameters), ct)).Select(JsonNode (runnable) =>
					new JsonObject
					{
						["name"] = string.Join('.',
							runnable.Function.Symbol.QualifiedName.Select(static part => part.Name)),
						["range"] = Protocol.ToRange(runnable.Name),
						["main"] = runnable.IsMain
					})
			]),
			_ => null
		};
	
	private async Task<JsonNode?> HoverAsync(JsonObject? parameters, CancellationToken ct) =>
		await FindAsync(parameters, ct) is { } hovered
			? new JsonObject
			{
				["contents"] = new JsonObject
				{
					["kind"] = hoversPlainText ? "plaintext" : "markdown",
					["value"] = hoversPlainText ? hovered.Description : $"```cella\n{hovered.Description}\n```"
				},
				["range"] = Protocol.ToRange(hovered.Location)
			}
			: null;
	
	private async Task<JsonNode?> DescribeAsync(JsonObject? parameters, CancellationToken ct) =>
		await _workspace.FindInModelAsync(GetUri(parameters), (int)parameters!["position"]!["line"]!,
			(int)parameters["position"]!["character"]!, ct) is var (reference, model)
			? SemanticTokens.EncodeDescription(reference, model)
			: null;
	
	private async Task<JsonNode?> FindDefinitionAsync(JsonObject? parameters, CancellationToken ct) =>
		await FindAsync(parameters, ct) is { Declaration: { } declaration } found
			? new JsonArray([
				..found.OverloadDeclarations.Prepend(declaration).Select(JsonNode (location) => new JsonObject
				{
					["uri"] = _workspace.UriOf(location.Source.FilePath),
					["range"] = Protocol.ToRange(location)
				})
			])
			: null;
	
	private async Task<JsonNode?> FindReferencesAsync(JsonObject? parameters, CancellationToken ct)
	{
		var position = parameters!["position"]!;
		var usages = await _workspace.FindUsagesAsync(GetUri(parameters), (int)position["line"]!,
			(int)position["character"]!, (bool?)parameters["context"]?["includeDeclaration"] ?? false, ct);
		
		return new JsonArray([
			..usages.Select(JsonNode (usage) => new JsonObject
			{
				["uri"] = _workspace.UriOf(usage.Source.FilePath),
				["range"] = Protocol.ToRange(usage)
			})
		]);
	}
	
	private async Task<JsonNode?> CompleteAsync(JsonObject? parameters, CancellationToken ct)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var offset = GetOffset(text, parameters);
		if ((string?)parameters?["context"]?["triggerCharacter"] == " " &&
		    !Completion.FollowsCompletionKeyword(text, offset))
			return new JsonObject { ["isIncomplete"] = false, ["items"] = new JsonArray() };
		
		var inserted = Completion.FindNameStart(text, offset) < offset ? 0 : Completion.Placeholder.Length;
		var probed = text.Insert(offset, Completion.Placeholder[..inserted]);
		return Completion.ToList(await _workspace.ProbeAsync(Protocol.ToPath(uri), probed, offset, ct), text, inserted);
	}
	
	private async Task<JsonNode?> FindVisibleGlobalsAsync(JsonObject? parameters, CancellationToken ct)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var offset = Debugging.FindStatementStart(text, GetOffset(text, parameters));
		var probed = text.Insert(offset, Completion.Placeholder + "\n");
		var probe = await _workspace.ProbeAsync(Protocol.ToPath(uri), probed, offset, ct);
		return probe?.Place == CompletionPlace.Name
			? new JsonArray([..Debugging.DescribeGlobals(probe.Candidates)])
			: new JsonArray();
	}
	
	private async Task<JsonNode?> FindValueAsync(JsonObject? parameters, CancellationToken ct)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var (references, _) = await _workspace.GetTokensAsync(uri, ct);
		if (Debugging.FindValue(references, GetOffset(text, parameters)) is not { } value)
			return null;
		
		var result = new JsonObject { ["range"] = Protocol.ToRange(Debugging.ExtendValue(references, value, text)) };
		if (value is not { Symbol: GlobalSymbol, Declaration: { } declaration })
			return result;
		
		var probe = await _workspace.ProbeAsync(Protocol.ToPath(uri), text, value.Location.Range.Start, ct);
		result["global"] = probe?.Candidates
			.Select(static candidate => candidate.Global)
			.OfType<GlobalInfo>()
			.Where(global => Debugging.IsDeclaredAt(global, declaration))
			.Select(Debugging.Describe)
			.FirstOrDefault();
		
		return result;
	}
	
	private async Task<JsonNode?> FindCodeActionsAsync(JsonObject? parameters, CancellationToken ct)
	{
		var (diagnostics, file, project, text) = await _workspace.GetFileAsync(GetUri(parameters), ct);
		if (text is null)
			return new JsonArray();
		
		var range = parameters!["range"]!;
		var requested = new TextRange(
			Protocol.ToOffset(text, (int)range["start"]!["line"]!, (int)range["start"]!["character"]!),
			Protocol.ToOffset(text, (int)range["end"]!["line"]!, (int)range["end"]!["character"]!));
		
		var uri = GetUri(parameters);
		var expanded = new List<Diagnostic>();
		foreach (var diagnostic in diagnostics)
			expanded.Add(CodeActions.Touches(diagnostic, requested)
				? await ExpandRenamesAsync(uri, diagnostic, ct)
				: diagnostic);
		
		JsonNode? moveAction = null;
		if (file is not null && project is not null &&
		    TypeMoves.Find(project, file, requested, _workspace.UriOf) is { } move)
			moveAction = resolvesActions ? Defer(move, uri) : await CompleteMoveAsync(move, diagnostics, ct);
		
		return new JsonArray([
			..CodeActions.FromDiagnostics(expanded, requested, _workspace.UriOf),
			..file is null
				? []
				: Refactorings.Find(file, requested).Select(fix =>
					CodeActions.ToAction(fix, CodeActions.Rewrite, _workspace.UriOf)),
			..moveAction is null ? [] : new[] { moveAction }
		]);
	}
	
	private static JsonObject Defer(TypeMove move, string uri)
	{
		var action = (JsonObject)move.Action.DeepClone();
		action.Remove("edit");
		action["data"] = new JsonObject { ["uri"] = uri, ["offset"] = move.Offset };
		return action;
	}
	
	private async Task<JsonNode?> ResolveCodeActionAsync(JsonObject? action, CancellationToken ct)
	{
		if (action?["data"] is not JsonObject { } data || (string?)data["uri"] is not { } uri ||
		    (int?)data["offset"] is not { } offset)
			return action;
		
		var (diagnostics, file, project, _) = await _workspace.GetFileAsync(uri, ct);
		if (file is null || project is null ||
		    TypeMoves.Find(project, file, new(offset, offset), _workspace.UriOf) is not { } move)
			return action;
		
		return await CompleteMoveAsync(move, diagnostics, ct);
	}
	
	private async Task<JsonObject> CompleteMoveAsync(TypeMove move, IReadOnlyList<Diagnostic> diagnostics,
		CancellationToken ct)
	{
		var source = move.Source;
		var removal = move.Removal;
		var after = source.GetText(new(0, removal.Start)).ToString() +
		            source.GetText(new(removal.End, source.Length)).ToString();
		
		var unused = diagnostics
			.Where(static diagnostic => diagnostic.Lint == Lints.UnusedImport)
			.Select(static diagnostic => diagnostic.SourceLocation.Range)
			.ToHashSet();
		
		var end = -1;
		var edits = new List<TextEdit>();
		foreach (var edit in (await _workspace.CheckTextAsync(source.FilePath, after, ct))
		         .Where(diagnostic => diagnostic.Lint == Lints.UnusedImport &&
		                              diagnostic.SourceLocation.Range.End <= removal.Start &&
		                              !unused.Contains(diagnostic.SourceLocation.Range))
		         .SelectMany(static diagnostic => diagnostic.Fixes.Take(1))
		         .SelectMany(static fix => fix.Edits)
		         .OrderBy(static edit => edit.Location.Range.Start))
		{
			if (edit.Location.Range.Start < end || edit.Location.Range.End > removal.Start)
				continue;
			
			edits.Add(edit);
			end = edit.Location.Range.End;
		}
		
		TypeMoves.AddSourceEdits(move, edits);
		return move.Action;
	}
	
	private async Task<Diagnostic> ExpandRenamesAsync(string uri, Diagnostic diagnostic, CancellationToken ct)
	{
		if (diagnostic.Fixes.All(static fix => fix.Rename is null))
			return diagnostic;
		
		var (line, character) = Protocol.ToLineCharacter(diagnostic.SourceLocation.Source,
			diagnostic.SourceLocation.Range.Start);
		
		var usages = await _workspace.FindUsagesAsync(uri, line, character, true, ct);
		return diagnostic with
		{
			Fixes =
			[
				..diagnostic.Fixes.Select(fix => fix.Rename is { } name && usages.Count > 0
					? fix with { Edits = [..usages.Select(usage => new TextEdit(usage, name))] }
					: fix)
			]
		};
	}
	
	private async Task<JsonNode?> PrepareRenameAsync(JsonObject? parameters, CancellationToken ct)
	{
		var position = parameters!["position"]!;
		return await _workspace.FindRenameableAsync(GetUri(parameters), (int)position["line"]!,
			(int)position["character"]!, ct) is { } reference
			? new JsonObject
			{
				["range"] = Protocol.ToRange(reference.Location),
				["placeholder"] = reference.Location.GetText().ToString()
			}
			: null;
	}
	
	private async Task<JsonNode?> RenameAsync(JsonObject? parameters, CancellationToken ct)
	{
		var position = parameters!["position"]!;
		var (line, character) = ((int)position["line"]!, (int)position["character"]!);
		var newName = (string)parameters["newName"]!;
		if (await _workspace.FindRenameableAsync(GetUri(parameters), line, character, ct) is null)
			throw new RequestFailedException("This can't be renamed");
		
		if (!Workspace.IsName(newName))
			throw new RequestFailedException(TokenTypeInfo.GetKeyword(newName) is not null
				? $"'{newName}' is a keyword"
				: $"'{newName}' isn't a valid name");
		
		var usages = await _workspace.FindUsagesAsync(GetUri(parameters), line, character, true, ct);
		return CodeActions.ToWorkspaceEdit(usages.Select(usage => new TextEdit(usage, newName)), _workspace.UriOf);
	}
	
	private async Task<JsonNode?> HelpSignatureAsync(JsonObject? parameters, CancellationToken ct)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var probe = await _workspace.ProbeAsync(Protocol.ToPath(uri), text, GetOffset(text, parameters), ct);
		return probe?.Call is { } call ? Completion.ToSignatureHelp(call) : null;
	}
	
	private static int GetOffset(string text, JsonObject? parameters) => Protocol.ToOffset(text,
		(int)parameters!["position"]!["line"]!, (int)parameters["position"]!["character"]!);
	
	private async Task AnswerLaterAsync(JsonNode id, string key, Func<CancellationToken, Task<JsonNode?>> answer,
		CancellationTokenSource cancellation)
	{
		JsonObject response;
		try
		{
			response = Protocol.Response(id, await answer(cancellation.Token));
		}
		catch (OperationCanceledException)
		{
			response = Protocol.ErrorResponse(id, RequestCancelled, "Cancelled");
		}
		catch (RequestFailedException exception)
		{
			response = Protocol.ErrorResponse(id, RequestFailed, exception.Message);
		}
		catch (Exception exception)
		{
			response = Protocol.ErrorResponse(id, InternalError, exception.Message);
		}
		finally
		{
			_pending.TryRemove(key, out _);
			cancellation.Dispose();
		}
		
		try
		{
			await _connection.WriteAsync(response, CancellationToken.None);
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
		}
	}
	
	private static string GetUri(JsonObject? parameters) => (string)parameters!["textDocument"]!["uri"]!;
	
	private static bool ChangesProjects(JsonNode? change)
	{
		var path = (string?)change?["uri"] ?? "";
		return CellaProject.IsProjectFile(path) ||
		       (int?)change?["type"] is FileCreated or FileDeleted &&
		       !path.EndsWith(".ce", StringComparison.OrdinalIgnoreCase);
	}
	
	private string GetProjectText(JsonObject? parameters) =>
		(string?)parameters?["text"] ?? _workspace.GetText(GetUri(parameters)) ?? "";
	
	private Task<SymbolReference?> FindAsync(JsonObject? parameters, CancellationToken ct) => _workspace.FindAsync(
		GetUri(parameters), (int)parameters!["position"]!["line"]!, (int)parameters["position"]!["character"]!, ct);
	
	private static JsonArray FindLibraryRoots()
	{
		var installation = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
		IEnumerable<string> versions = installation is not null && Directory.Exists(installation)
			? Directory.EnumerateDirectories(installation).Select(static version => Path.Combine(version, "lib", "std"))
			: [];
		
		return new JsonArray([
			..versions.Prepend(Builder.StandardLibraryDirectory)
				.Where(Directory.Exists)
				.Distinct(Protocol.PathComparer)
				.Select(static root => JsonValue.Create(root))
		]);
	}
	
	private static JsonObject Capabilities() => new()
	{
		["capabilities"] = new JsonObject
		{
			["hoverProvider"] = true,
			["definitionProvider"] = true,
			["referencesProvider"] = true,
			["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
			["codeActionProvider"] = new JsonObject
			{
				["codeActionKinds"] = new JsonArray(CodeActions.QuickFix, CodeActions.Rewrite, TypeMoves.Kind),
				["resolveProvider"] = true
			},
			["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".", " ") },
			["signatureHelpProvider"] = new JsonObject
			{
				["triggerCharacters"] = new JsonArray("(", ","),
				["retriggerCharacters"] = new JsonArray(",")
			},
			["semanticTokensProvider"] = new JsonObject
			{
				["legend"] = SemanticTokens.Legend.DeepClone(),
				["full"] = true
			},
			["textDocumentSync"] = new JsonObject
			{
				["openClose"] = true,
				["change"] = FullSync,
				["save"] = new JsonObject { ["includeText"] = false }
			},
			["experimental"] = new JsonObject { ["projects"] = true, ["libraryRoots"] = FindLibraryRoots() }
		},
		["serverInfo"] = new JsonObject { ["name"] = "cella-lsp", ["version"] = SemanticVersion.Current.ToString() }
	};
}