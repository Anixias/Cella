using System.IO.Pipes;
using System.Text.Json.Nodes;
using Cella.LanguageServer;
using Xunit;

namespace Cella.Tests;

public sealed class LanguageServerTests : IAsyncDisposable
{
	private const string ValidSource = "mod test\n\nmain: fun -> i32 = 0\n";
	private const string InvalidSource = "mod test\n\nmain: fun -> i32 = true\n";
	
	private readonly string _directory =
		Path.Combine(Path.GetTempPath(), "cella-server-tests", Guid.NewGuid().ToString("N"));
	
	private readonly AnonymousPipeServerStream _clientOutput = new(PipeDirection.Out);
	private readonly AnonymousPipeServerStream _clientInput = new(PipeDirection.In);
	private readonly MessageConnection _client;
	private readonly Task<int> _server;
	private int nextId;
	
	public LanguageServerTests()
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(Path.Combine(_directory, "test.celp"), "OutputType = \"Executable\"\n");
		File.WriteAllText(SourcePath, ValidSource);
		
		var serverInput = new AnonymousPipeClientStream(PipeDirection.In, _clientOutput.ClientSafePipeHandle);
		var serverOutput = new AnonymousPipeClientStream(PipeDirection.Out, _clientInput.ClientSafePipeHandle);
		_client = new MessageConnection(_clientInput, _clientOutput);
		_server = Task.Run(() => new Server(serverInput, serverOutput, TimeSpan.Zero).RunAsync());
	}
	
	public async ValueTask DisposeAsync()
	{
		await _clientOutput.DisposeAsync();
		await _server.WaitAsync(TimeSpan.FromSeconds(30));
		await _clientInput.DisposeAsync();
		Directory.Delete(_directory, true);
	}
	
	private static CancellationToken Token => TestContext.Current.CancellationToken;
	
	private string SourcePath => Path.Combine(_directory, "main.ce");
	
	private string SourceUri => OperatingSystem.IsWindows()
		? $"file:///{char.ToLowerInvariant(SourcePath[0])}%3A{SourcePath[2..].Replace('\\', '/')}"
		: new Uri(SourcePath).AbsoluteUri;
	
	private async Task<int> RequestAsync(string method, JsonObject parameters)
	{
		var id = ++nextId;
		await _client.WriteAsync(new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id,
			["method"] = method,
			["params"] = parameters
		}, Token);
		
		return id;
	}
	
	private Task NotifyAsync(string method, JsonObject parameters) =>
		_client.WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, Token);
	
	private async Task<JsonObject> ReadUntilAsync(Func<JsonObject, bool> predicate)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
		timeout.CancelAfter(TimeSpan.FromSeconds(60));
		while (await _client.ReadAsync(timeout.Token) is { } message)
		{
			if (predicate(message))
				return message;
		}
		
		throw new EndOfStreamException();
	}
	
	private Task<JsonObject> ReadResponseAsync(int id) =>
		ReadUntilAsync(message => message["id"] is { } received && (int)received == id);
	
	private Task<JsonObject> ReadDiagnosticsAsync(Func<JsonArray, bool> predicate) =>
		ReadUntilAsync(message => (string?)message["method"] == "textDocument/publishDiagnostics" &&
		                          (string?)message["params"]!["uri"] == SourceUri &&
		                          predicate((JsonArray)message["params"]!["diagnostics"]!));
	
	private async Task InitializeAsync()
	{
		var id = await RequestAsync("initialize", new JsonObject
		{
			["rootUri"] = new Uri(_directory).AbsoluteUri,
			["capabilities"] = new JsonObject()
		});
		
		var response = await ReadResponseAsync(id);
		Assert.Equal(1, (int)response["result"]!["capabilities"]!["textDocumentSync"]!["change"]!);
		await NotifyAsync("initialized", new JsonObject());
	}
	
	private async Task<int> ShutDownAsync()
	{
		await ReadResponseAsync(await RequestAsync("shutdown", new JsonObject()));
		await NotifyAsync("exit", new JsonObject());
		return await _server.WaitAsync(TimeSpan.FromSeconds(30), Token);
	}
	
	[Fact]
	public async Task PublishesDiagnosticsForEdits()
	{
		await InitializeAsync();
		await NotifyAsync("textDocument/didOpen", new JsonObject
		{
			["textDocument"] = new JsonObject
			{
				["uri"] = SourceUri,
				["languageId"] = "cella",
				["version"] = 1,
				["text"] = InvalidSource
			}
		});
		
		var published = await ReadDiagnosticsAsync(static diagnostics => diagnostics.Count > 0);
		var error = Assert.Single((JsonArray)published["params"]!["diagnostics"]!)!;
		Assert.Equal(1, (int)error["severity"]!);
		Assert.Equal(2, (int)error["range"]!["start"]!["line"]!);
		Assert.Equal(19, (int)error["range"]!["start"]!["character"]!);
		Assert.Equal(23, (int)error["range"]!["end"]!["character"]!);
		
		await NotifyAsync("textDocument/didChange", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = SourceUri, ["version"] = 2 },
			["contentChanges"] = new JsonArray(new JsonObject { ["text"] = ValidSource })
		});
		
		await ReadDiagnosticsAsync(static diagnostics => diagnostics.Count == 0);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ReadsEditorUris()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Equal(@"c:\Users\a b\main.ce", Protocol.ToPath("file:///c%3A/Users/a%20b/main.ce"));
			Assert.Equal(@"\\server\share\main.ce", Protocol.ToPath("file://server/share/main.ce"));
		}
		else
			Assert.Equal("/home/a b/main.ce", Protocol.ToPath("file:///home/a%20b/main.ce"));
		
		await InitializeAsync();
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task RejectsUnknownRequests()
	{
		await InitializeAsync();
		var response = await ReadResponseAsync(await RequestAsync("textDocument/unknown", new JsonObject()));
		Assert.Equal(-32601, (int)response["error"]!["code"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
}