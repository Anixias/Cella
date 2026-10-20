using System.Text.Json.Nodes;
using Cella.Compiler.Projects;
using Cella.Core.Binding;
using Cella.Core.Symbols;

namespace Cella.LanguageServer;

public sealed class Server
{
	private const int MethodNotFound = -32601;
	private const int InternalError = -32603;
	private const int RequestCancelled = -32800;
	private const int FullSync = 1;
	
	private readonly MessageConnection _connection;
	private readonly Workspace _workspace;
	private bool hoversPlainText;
	
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
				
				if (id is not null && AnswerLater(method, message["params"] as JsonObject) is { } answer)
				{
					_ = AnswerLaterAsync(id, answer, ct);
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
				
				_workspace.Initialize(parameters);
				return (true, Capabilities());
			
			case "textDocument/didOpen":
				_workspace.Open(GetUri(parameters), (string)parameters!["textDocument"]!["text"]!);
				return (true, null);
			
			case "textDocument/didChange":
				var changes = (JsonArray)parameters!["contentChanges"]!;
				_workspace.Change(GetUri(parameters), (string)changes[^1]!["text"]!);
				return (true, null);
			
			case "textDocument/didClose":
				_workspace.Close(GetUri(parameters));
				return (true, null);
			
			case "textDocument/didSave" or "workspace/didChangeWatchedFiles":
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
	
	private Func<Task<JsonNode?>>? AnswerLater(string method, JsonObject? parameters) => method switch
	{
		"textDocument/semanticTokens/full" => async () =>
		{
			var uri = GetUri(parameters);
			if (CellaProject.IsProjectFile(Protocol.ToPath(uri)))
				return _workspace.GetText(uri) is { } project
					? SemanticTokens.EncodeSettings(project, SettingKeys.Find(project))
					: null;
			
			var (references, keywords) = await _workspace.GetTokensAsync(uri);
			return SemanticTokens.Encode(references, keywords);
		},
		"textDocument/hover" => () => HoverAsync(parameters),
		"textDocument/definition" => () => FindDefinitionAsync(parameters),
		"textDocument/references" => () => FindReferencesAsync(parameters),
		"textDocument/completion" => () => CompleteAsync(parameters),
		"textDocument/signatureHelp" => () => HelpSignatureAsync(parameters),
		"cella/visibleGlobals" => () => FindVisibleGlobalsAsync(parameters),
		"cella/valueAt" => () => FindValueAsync(parameters),
		"cella/runnables" => async () => new JsonArray([
			..(await _workspace.GetRunnablesAsync(GetUri(parameters))).Select(JsonNode (runnable) => new JsonObject
			{
				["name"] = string.Join('.', runnable.Function.Symbol.QualifiedName.Select(static part => part.Name)),
				["range"] = Protocol.ToRange(runnable.Name),
				["main"] = runnable.IsMain
			})
		]),
		_ => null
	};
	
	private async Task<JsonNode?> HoverAsync(JsonObject? parameters) => await FindAsync(parameters) is { } hovered
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
	
	private async Task<JsonNode?> FindDefinitionAsync(JsonObject? parameters) =>
		(await FindAsync(parameters))?.Declaration is { } declaration
			? new JsonArray(new JsonObject
			{
				["uri"] = _workspace.UriOf(declaration.Source.FilePath),
				["range"] = Protocol.ToRange(declaration)
			})
			: null;
	
	private async Task<JsonNode?> FindReferencesAsync(JsonObject? parameters)
	{
		var position = parameters!["position"]!;
		var usages = await _workspace.FindUsagesAsync(GetUri(parameters), (int)position["line"]!,
			(int)position["character"]!, (bool?)parameters["context"]?["includeDeclaration"] ?? false);
		
		return new JsonArray([
			..usages.Select(JsonNode (usage) => new JsonObject
			{
				["uri"] = _workspace.UriOf(usage.Source.FilePath),
				["range"] = Protocol.ToRange(usage)
			})
		]);
	}
	
	private async Task<JsonNode?> CompleteAsync(JsonObject? parameters)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var offset = GetOffset(text, parameters);
		var probed = Completion.FindNameStart(text, offset) < offset
			? text
			: text.Insert(offset, Completion.Placeholder);
		
		return Completion.ToList(await _workspace.ProbeAsync(Protocol.ToPath(uri), probed, offset));
	}
	
	private async Task<JsonNode?> FindVisibleGlobalsAsync(JsonObject? parameters)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var offset = Debugging.FindStatementStart(text, GetOffset(text, parameters));
		var probed = text.Insert(offset, Completion.Placeholder + "\n");
		var probe = await _workspace.ProbeAsync(Protocol.ToPath(uri), probed, offset);
		return probe?.Place == CompletionPlace.Name
			? new JsonArray([..Debugging.DescribeGlobals(probe.Candidates)])
			: new JsonArray();
	}
	
	private async Task<JsonNode?> FindValueAsync(JsonObject? parameters)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var (references, _) = await _workspace.GetTokensAsync(uri);
		if (Debugging.FindValue(references, GetOffset(text, parameters)) is not { } value)
			return null;
		
		var result = new JsonObject { ["range"] = Protocol.ToRange(Debugging.ExtendValue(references, value, text)) };
		if (value is not { Symbol: GlobalSymbol, Declaration: { } declaration })
			return result;
		
		var probe = await _workspace.ProbeAsync(Protocol.ToPath(uri), text, value.Location.Range.Start);
		result["global"] = probe?.Candidates
			.Select(static candidate => candidate.Global)
			.OfType<GlobalInfo>()
			.Where(global => Debugging.IsDeclaredAt(global, declaration))
			.Select(Debugging.Describe)
			.FirstOrDefault();
		
		return result;
	}
	
	private async Task<JsonNode?> HelpSignatureAsync(JsonObject? parameters)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var probe = await _workspace.ProbeAsync(Protocol.ToPath(uri), text, GetOffset(text, parameters));
		return probe?.Call is { } call ? Completion.ToSignatureHelp(call) : null;
	}
	
	private static int GetOffset(string text, JsonObject? parameters) => Protocol.ToOffset(text,
		(int)parameters!["position"]!["line"]!, (int)parameters["position"]!["character"]!);
	
	private async Task AnswerLaterAsync(JsonNode id, Func<Task<JsonNode?>> answer, CancellationToken ct)
	{
		JsonObject response;
		try
		{
			response = Protocol.Response(id, await answer());
		}
		catch (OperationCanceledException)
		{
			response = Protocol.ErrorResponse(id, RequestCancelled, "The server is shutting down");
		}
		catch (Exception exception)
		{
			response = Protocol.ErrorResponse(id, InternalError, exception.Message);
		}
		
		try
		{
			await _connection.WriteAsync(response, ct);
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException
			                                  or OperationCanceledException)
		{
		}
	}
	
	private static string GetUri(JsonObject? parameters) => (string)parameters!["textDocument"]!["uri"]!;
	
	private string GetProjectText(JsonObject? parameters) =>
		(string?)parameters?["text"] ?? _workspace.GetText(GetUri(parameters)) ?? "";
	
	private Task<SymbolReference?> FindAsync(JsonObject? parameters) => _workspace.FindAsync(GetUri(parameters),
		(int)parameters!["position"]!["line"]!, (int)parameters["position"]!["character"]!);
	
	private static JsonObject Capabilities() => new()
	{
		["capabilities"] = new JsonObject
		{
			["hoverProvider"] = true,
			["definitionProvider"] = true,
			["referencesProvider"] = true,
			["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".") },
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
			["experimental"] = new JsonObject { ["projects"] = true }
		},
		["serverInfo"] = new JsonObject { ["name"] = "cella-lsp", ["version"] = SemanticVersion.Current.ToString() }
	};
}