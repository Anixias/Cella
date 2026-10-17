using System.Text.Json.Nodes;

namespace Cella.LanguageServer;

public sealed class Server
{
	private const int MethodNotFound = -32601;
	private const int InternalError = -32603;
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
	
	private static string GetUri(JsonObject? parameters) => (string)parameters!["textDocument"]!["uri"]!;
	
	private static JsonObject Capabilities() => new()
	{
		["capabilities"] = new JsonObject
		{
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