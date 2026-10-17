using System.Text.Json.Nodes;
using Cella.Core.Binding;

namespace Cella.LanguageServer;

public sealed class Server
{
	private const int MethodNotFound = -32601;
	private const int InternalError = -32603;
	private const int RequestCancelled = -32800;
	private const int FullSync = 1;
	
	private readonly MessageConnection _connection;
	private readonly Workspace _workspace;
	
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
				_workspace.Initialize(parameters);
				return (true, Capabilities());
			
			case "textDocument/didOpen":
				_workspace.Open(GetUri(parameters), (string)parameters!["textDocument"]!["text"]!);
				return (true, null);
			
			case "textDocument/didChange":
				var changes = (JsonArray)parameters!["contentChanges"]!;
				_workspace.Change(GetUri(parameters), (string)changes[^1]!["text"]!);
				return (true, null);
			
			case "textDocument/hover":
				return (true, Find(parameters) is { } hovered
					? new JsonObject
					{
						["contents"] = new JsonObject
						{
							["kind"] = "markdown",
							["value"] = $"```cella\n{hovered.Description}\n```"
						},
						["range"] = Protocol.ToRange(hovered.Location)
					}
					: null);
			
			case "textDocument/definition":
				return (true, Find(parameters)?.Declaration is { } declaration
					? new JsonObject
					{
						["uri"] = _workspace.UriOf(declaration.Source.FilePath),
						["range"] = Protocol.ToRange(declaration)
					}
					: null);
			
			case "textDocument/didClose":
				_workspace.Close(GetUri(parameters));
				return (true, null);
			
			case "textDocument/didSave" or "workspace/didChangeWatchedFiles":
				_workspace.RequestCheck();
				return (true, null);
			
			case "initialized" or "shutdown":
				return (true, null);
			
			default:
				return (method.StartsWith("$/", StringComparison.Ordinal), null);
		}
	}
	
	private Func<Task<JsonNode?>>? AnswerLater(string method, JsonObject? parameters) => method switch
	{
		"textDocument/semanticTokens/full" => async () =>
			SemanticTokens.Encode(await _workspace.GetReferencesAsync(GetUri(parameters))),
		"textDocument/completion" => () => CompleteAsync(parameters),
		"textDocument/signatureHelp" => () => HelpSignatureAsync(parameters),
		_ => null
	};
	
	private async Task<JsonNode?> CompleteAsync(JsonObject? parameters)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var offset = GetOffset(text, parameters);
		var start = Completion.FindNameStart(text, offset);
		var probed = Completion.Repair(start < offset ? text : text.Insert(offset, Completion.Placeholder), offset);
		return Completion.ToList(await _workspace.ProbeAsync(Protocol.ToPath(uri), probed, offset), text, start);
	}
	
	private async Task<JsonNode?> HelpSignatureAsync(JsonObject? parameters)
	{
		var uri = GetUri(parameters);
		if (_workspace.GetText(uri) is not { } text)
			return null;
		
		var offset = GetOffset(text, parameters);
		if (Completion.FindCall(text, offset) is not var (nameEnd, callee, argument))
			return null;
		
		var probed = Completion.IsEmptyArgument(text, offset) ? text.Insert(offset, Completion.Placeholder) : text;
		return await _workspace.ProbeAsync(Protocol.ToPath(uri), probed, nameEnd) is { } probe
			? Completion.ToSignatureHelp(probe, callee, argument)
			: null;
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
	
	private SymbolReference? Find(JsonObject? parameters) => _workspace.Find(GetUri(parameters),
		(int)parameters!["position"]!["line"]!, (int)parameters["position"]!["character"]!);
	
	private static JsonObject Capabilities() => new()
	{
		["capabilities"] = new JsonObject
		{
			["hoverProvider"] = true,
			["definitionProvider"] = true,
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
			}
		},
		["serverInfo"] = new JsonObject { ["name"] = "cella-lsp", ["version"] = "0.1.0" }
	};
}