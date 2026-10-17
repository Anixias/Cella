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
	
	private async Task<JsonObject> InitializeAsync()
	{
		var id = await RequestAsync("initialize", new JsonObject
		{
			["rootUri"] = new Uri(_directory).AbsoluteUri,
			["capabilities"] = new JsonObject()
		});
		
		var response = await ReadResponseAsync(id);
		var capabilities = (JsonObject)response["result"]!["capabilities"]!;
		Assert.Equal(1, (int)capabilities["textDocumentSync"]!["change"]!);
		await NotifyAsync("initialized", new JsonObject());
		return capabilities;
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
	
	private const string NavigationSource = """
	                                        mod test
	                                        
	                                        Point: rec
	                                        {
	                                        	x: i32
	                                        	y: i32
	                                        }
	                                        
	                                        sum: fun(imm point: Point) -> i32 = point.x + point.y
	                                        
	                                        main: fun -> i32
	                                        {
	                                        	val origin = Point(1, 2)
	                                        	ret sum(imm origin)
	                                        }
	                                        
	                                        """;
	
	private static JsonObject Position(int line, int character) => new()
	{
		["textDocument"] = new JsonObject(),
		["position"] = new JsonObject { ["line"] = line, ["character"] = character }
	};
	
	private async Task<JsonNode?> AskAsync(string method, int line, int character)
	{
		var parameters = Position(line, character);
		parameters["textDocument"]!["uri"] = SourceUri;
		return (await ReadResponseAsync(await RequestAsync(method, parameters)))["result"];
	}
	
	private async Task<string> HoverAsync(int line, int character)
	{
		for (var attempt = 0; attempt < 200; attempt++)
		{
			if (await AskAsync("textDocument/hover", line, character) is { } result)
				return (string)result["contents"]!["value"]!;
			
			await Task.Delay(50, Token);
		}
		
		throw new TimeoutException();
	}
	
	private static string Code(string text) => $"```cella\n{text}\n```";
	
	[Fact]
	public async Task AnswersHoverAndDefinition()
	{
		await File.WriteAllTextAsync(SourcePath, NavigationSource, Token);
		await InitializeAsync();
		
		Assert.Equal(Code("sum: fun(imm point: Point) -> i32"), await HoverAsync(13, 6));
		Assert.Equal(Code("val origin: Point"), await HoverAsync(13, 14));
		Assert.Equal(Code("x: i32"), await HoverAsync(8, 42));
		Assert.Equal(Code("Point: rec"), await HoverAsync(8, 21));
		Assert.Equal(Code("Point: rec"), await HoverAsync(12, 15));
		
		var function = await AskAsync("textDocument/definition", 13, 6);
		Assert.Equal(8, (int)function!["range"]!["start"]!["line"]!);
		Assert.Equal(0, (int)function["range"]!["start"]!["character"]!);
		Assert.Equal(3, (int)function["range"]!["end"]!["character"]!);
		
		var type = await AskAsync("textDocument/definition", 12, 15);
		Assert.Equal(2, (int)type!["range"]!["start"]!["line"]!);
		
		var field = await AskAsync("textDocument/definition", 8, 42);
		Assert.Equal(4, (int)field!["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)field["range"]!["start"]!["character"]!);
		
		Assert.Null(await AskAsync("textDocument/definition", 8, 30));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesNamesInTheOpenText()
	{
		await File.WriteAllTextAsync(SourcePath, NavigationSource, Token);
		Assert.Equal(
		[
			"2:0:5 class declaration",
			"4:1:1 property declaration",
			"4:4:3 keyword",
			"5:1:1 property declaration",
			"5:4:3 keyword",
			"8:0:5 function declaration",
			"8:15:5 parameter declaration",
			"8:22:5 class",
			"8:32:3 keyword",
			"8:38:5 parameter",
			"8:44:1 property",
			"8:48:5 parameter",
			"8:54:1 property",
			"10:0:4 function declaration",
			"10:13:3 keyword",
			"12:5:6 variable declaration readonly",
			"12:14:5 class",
			"13:5:5 function",
			"13:15:6 variable readonly"
		], await ClassifyAsync(NavigationSource.Replace("sum", "total")));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesOperatorsConstantsAndTypeArguments()
	{
		Assert.Equal(
		[
			"2:0:3 class declaration",
			"2:4:1 typeParameter declaration",
			"4:1:1 property declaration",
			"4:4:1 typeParameter",
			"5:1:1 method declaration",
			"5:11:1 parameter declaration",
			"5:14:3 class",
			"5:18:1 typeParameter",
			"5:26:1 parameter declaration",
			"5:29:3 class",
			"5:33:1 typeParameter",
			"5:40:1 typeParameter",
			"5:44:1 parameter",
			"5:46:1 property",
			"8:0:5 variable declaration readonly static",
			"8:13:3 keyword",
			"10:0:4 function declaration",
			"10:13:3 keyword",
			"12:7:5 variable declaration readonly static",
			"13:5:4 variable declaration",
			"13:11:5 keyword",
			"13:17:4 keyword",
			"14:5:1 variable declaration readonly",
			"14:9:3 class",
			"14:13:3 keyword",
			"14:18:3 keyword",
			"14:22:5 variable readonly static",
			"15:5:1 variable declaration readonly",
			"15:9:1 variable readonly",
			"15:11:1 method",
			"15:13:1 variable readonly",
			"16:5:5 variable readonly static"
		], await ClassifyAsync("""
		                       mod test
		                       
		                       Vec[T: copy]: rec
		                       {
		                       	x: T
		                       	+: op(imm a: Vec[T], imm b: Vec[T]) -> T = a.x
		                       }
		                       
		                       limit: const i32 = 4
		                       
		                       main: fun -> i32
		                       {
		                       	const count = 3
		                       	var name: array[char, 20] = undef
		                       	val a = Vec[i64](i64(count))
		                       	val b = a + a
		                       	ret limit
		                       }
		                       
		                       """));
		
		Assert.Equal(Code("+: op(imm a: Vec[i64], imm b: Vec[i64]) -> i64"), await HoverAsync(15, 11));
		Assert.Equal(Code("const count: i32"), await HoverAsync(14, 22));
		Assert.Equal(Code("char"), await HoverAsync(13, 17));
		
		var operation = await AskAsync("textDocument/definition", 15, 11);
		Assert.Equal(5, (int)operation!["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)operation["range"]!["start"]!["character"]!);
		
		var parameter = await AskAsync("textDocument/definition", 4, 4);
		Assert.Equal(2, (int)parameter!["range"]!["start"]!["line"]!);
		Assert.Equal(4, (int)parameter["range"]!["start"]!["character"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private async Task<List<string>> ClassifyAsync(string text)
	{
		var legend = (await InitializeAsync())["semanticTokensProvider"]!["legend"]!;
		var types = ((JsonArray)legend["tokenTypes"]!).Select(static type => (string)type!).ToArray();
		var modifiers = ((JsonArray)legend["tokenModifiers"]!).Select(static modifier => (string)modifier!).ToArray();
		
		await NotifyAsync("textDocument/didOpen", new JsonObject
		{
			["textDocument"] = new JsonObject
			{
				["uri"] = SourceUri,
				["languageId"] = "cella",
				["version"] = 1,
				["text"] = text
			}
		});
		
		var id = await RequestAsync("textDocument/semanticTokens/full",
			new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = SourceUri } });
		
		var data = ((JsonArray)(await ReadResponseAsync(id))["result"]!["data"]!).Select(static n => (int)n!).ToArray();
		var (line, character) = (0, 0);
		var tokens = new List<string>();
		for (var i = 0; i < data.Length; i += 5)
		{
			character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
			line += data[i];
			var applied = modifiers.Where((_, bit) => (data[i + 4] & (1 << bit)) != 0);
			tokens.Add(string.Join(' ', [$"{line}:{character}:{data[i + 2]}", types[data[i + 3]], ..applied]));
		}
		
		return tokens;
	}
	
	[Fact]
	public async Task NamesDereferencesAndRecordKinds()
	{
		var tokens = await ClassifyAsync("""
		                                 mod test
		                                 
		                                 Point: rec
		                                 {
		                                 	x: i32
		                                 }
		                                 
		                                 Box: rec
		                                 {
		                                 	inner: Point
		                                 
		                                 	*: op(imm self) -> imm[Point] = self.inner
		                                 }
		                                 
		                                 View: ref rec
		                                 {
		                                 	point: imm[Point]
		                                 }
		                                 
		                                 Color: enum
		                                 {
		                                 	red
		                                 	green
		                                 }
		                                 
		                                 main: fun -> i32
		                                 {
		                                 	val b = Box(Point(2))
		                                 	val p = Point(1)
		                                 	val v = View(imm p)
		                                 	val c = Color.red
		                                 	ret b.x + v.point.x
		                                 }
		                                 
		                                 """);
		
		Assert.Contains("2:0:5 class declaration", tokens);
		Assert.Contains("14:0:4 struct declaration", tokens);
		Assert.Contains("19:0:5 enum declaration", tokens);
		Assert.Contains("31:5:1 variable readonly", tokens);
		Assert.Contains("31:6:1 method", tokens);
		Assert.Contains("31:11:1 variable readonly", tokens);
		Assert.DoesNotContain(tokens, static token => token.StartsWith("31:12:1 "));
		
		Assert.Equal(Code("*: op(imm self) -> imm[Point]"), await HoverAsync(31, 6));
		Assert.Equal(Code("val b: Box"), await HoverAsync(31, 5));
		Assert.Equal(Code("View: ref rec"), await HoverAsync(29, 9));
		
		var dereference = await AskAsync("textDocument/definition", 31, 6);
		Assert.Equal(11, (int)dereference!["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)dereference["range"]!["start"]!["character"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task FadesUnreachableCode()
	{
		await InitializeAsync();
		await NotifyAsync("textDocument/didOpen", new JsonObject
		{
			["textDocument"] = new JsonObject
			{
				["uri"] = SourceUri,
				["languageId"] = "cella",
				["version"] = 1,
				["text"] = """
				           mod test
				           
				           main: fun -> i32
				           {
				           	ret 0
				           	val unused = 1
				           	ret unused
				           }
				           
				           """
			}
		});
		
		var published = await ReadDiagnosticsAsync(static diagnostics => diagnostics.Count > 0);
		var diagnostics = (JsonArray)published["params"]!["diagnostics"]!;
		Assert.Equal(2, diagnostics.Count);
		
		var warning = diagnostics.Single(static diagnostic => (int)diagnostic!["severity"]! == 2)!;
		Assert.Equal(5, (int)warning["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)warning["range"]!["end"]!["character"]!);
		
		var faded = diagnostics.Single(static diagnostic => (int)diagnostic!["severity"]! == 4)!;
		Assert.Equal(1, (int)faded["tags"]![0]!);
		Assert.Equal(5, (int)faded["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)faded["range"]!["start"]!["character"]!);
		Assert.Equal(6, (int)faded["range"]!["end"]!["line"]!);
		Assert.Equal(11, (int)faded["range"]!["end"]!["character"]!);
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
	
	private const string CompletionSource = """
	                                        mod test
	                                        
	                                        Point: rec
	                                        {
	                                        	x: i32
	                                        
	                                        	length: fun(imm self) -> i32 = self.x
	                                        	shift: fun(mut self, by: i32) = self.x = self.x + by
	                                        }
	                                        
	                                        scale: fun(mut target: Point, factor: i32) = target.x = target.x * factor
	                                        scale: fun(mut target: Point, imm by: Point) = target.x = target.x * by.x
	                                        move: fun(mut target: Point, imm offset: Point) = target.x = offset.x
	                                        twice: fun(value: i32) -> i32 = value * 2
	                                        apply: fun(f: fun[i32 -> i32], value: i32) -> i32 = f(value)
	                                        
	                                        main: fun -> i32
	                                        {
	                                        	var p = Point(1)
	                                        	val q = Point(2)
	                                        	CURSOR
	                                        	ret 0
	                                        }
	                                        
	                                        """;
	
	private int documentVersion;
	
	private async Task SetTextAsync(string text)
	{
		documentVersion++;
		if (documentVersion == 1)
			await NotifyAsync("textDocument/didOpen", new JsonObject
			{
				["textDocument"] = new JsonObject
				{
					["uri"] = SourceUri,
					["languageId"] = "cella",
					["version"] = documentVersion,
					["text"] = text
				}
			});
		else
			await NotifyAsync("textDocument/didChange", new JsonObject
			{
				["textDocument"] = new JsonObject { ["uri"] = SourceUri, ["version"] = documentVersion },
				["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text })
			});
	}
	
	private async Task<JsonNode?> AskAtCursorAsync(string method, string line)
	{
		var text = CompletionSource.Replace("CURSOR", line);
		var offset = text.IndexOf('|');
		text = text.Remove(offset, 1);
		await SetTextAsync(text);
		var lineNumber = text[..offset].Count(static c => c == '\n');
		return await AskAsync(method, lineNumber, offset - (text.LastIndexOf('\n', offset - 1) + 1));
	}
	
	private static Dictionary<string, JsonNode> ItemsOf(JsonNode? list) => ((JsonArray)list!["items"]!)
		.ToDictionary(static item => (string)item!["label"]!, static item => item!);
	
	[Fact]
	public async Task CompletesNamesMembersAndTypes()
	{
		await InitializeAsync();
		
		var names = ItemsOf(await AskAtCursorAsync("textDocument/completion", "|"));
		Assert.Equal("var p: Point", (string)names["p"]["detail"]!);
		Assert.Equal("move(mut ${1}, imm ${2})$0", (string)names["move"]["insertText"]!);
		Assert.Equal("scale($0)", (string)names["scale"]["insertText"]!);
		Assert.EndsWith("(+1 overload)", (string)names["scale"]["detail"]!);
		Assert.Equal("twice(${1})$0", (string)names["twice"]["insertText"]!);
		Assert.Contains("val", names.Keys);
		Assert.Contains("Point", names.Keys);
		
		var members = ItemsOf(await AskAtCursorAsync("textDocument/completion", "p.|"));
		Assert.Equal(["length", "shift", "x"], members.Keys.Order());
		Assert.Equal("length()$0", (string)members["length"]["insertText"]!);
		Assert.Equal("shift(${1})$0", (string)members["shift"]["insertText"]!);
		
		var values = ItemsOf(await AskAtCursorAsync("textDocument/completion", "val r = apply(tw|, 1)"));
		Assert.Equal("twice", (string)values["twice"]["insertText"]!);
		
		var types = ItemsOf(await AskAtCursorAsync("textDocument/completion", "val v: |"));
		Assert.Contains("Point", types.Keys);
		Assert.Contains("i32", types.Keys);
		Assert.DoesNotContain("p", types.Keys);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task CompletesBelowBrokenLines()
	{
		await InitializeAsync();
		var members = ItemsOf(await AskAtCursorAsync("textDocument/completion", "val broken = )\n\tp.|"));
		Assert.Equal(["length", "shift", "x"], members.Keys.Order());
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task HelpsWithSignatures()
	{
		await InitializeAsync();
		
		var overloads = await AskAtCursorAsync("textDocument/signatureHelp", "scale(mut p, |)");
		Assert.Equal(1, (int)overloads!["activeParameter"]!);
		Assert.Equal(
		[
			"scale: fun(mut target: Point, factor: i32)",
			"scale: fun(mut target: Point, imm by: Point)"
		], ((JsonArray)overloads["signatures"]!).Select(static signature => (string)signature!["label"]!));
		
		var method = await AskAtCursorAsync("textDocument/signatureHelp", "p.shift(|)");
		var signature = Assert.Single((JsonArray)method!["signatures"]!)!;
		Assert.Equal("shift: fun(by: i32)", (string)signature["label"]!);
		Assert.Equal(11, (int)signature["parameters"]![0]!["label"]![0]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task CompletesUsePaths()
	{
		await File.WriteAllTextAsync(Path.Combine(_directory, "physics.ce"), """
		                                                                     mod game.physics
		                                                                     
		                                                                     step: pub fun -> i32 = 1
		                                                                     Body: pub rec
		                                                                     {
		                                                                     	mass: i32
		                                                                     }
		                                                                     
		                                                                     """, Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, "solver.ce"), "mod game.physics.solver\n", Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, "geometry.ce"), "mod game.geometry\n", Token);
		await InitializeAsync();
		
		async Task<IEnumerable<string>> CompleteUseAsync(string path)
		{
			var text = $"mod test\n\nuse {path}\n\nmain: fun -> i32 = 0\n";
			await SetTextAsync(text);
			var list = await AskAsync("textDocument/completion", 2, 4 + path.Length);
			return ((JsonArray)list!["items"]!).Select(static item => item!)
				.OrderBy(static item => (string)item["sortText"]!, StringComparer.Ordinal)
				.Select(static item => (string)item["label"]!);
		}
		
		Assert.Equal(["game", "test"], await CompleteUseAsync(""));
		Assert.Equal(["geometry", "physics"], await CompleteUseAsync("game."));
		Assert.Equal(["solver", "Body", "step"], await CompleteUseAsync("game.physics."));
		Assert.Equal(["solver", "Body"], await CompleteUseAsync("game.physics.[step, "));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task CompletesCasePatterns()
	{
		const string source = """
		                      mod test
		                      
		                      Color: enum
		                      {
		                      	red
		                      	green
		                      }
		                      
		                      main: fun -> i32
		                      {
		                      	val c = Color.red
		                      	val n = match c
		                      	{
		                      		Color.red => 1
		                      		ARM
		                      	}
		                      
		                      	ret if c is r { 1 } else { n }
		                      }
		                      
		                      """;
		
		await InitializeAsync();
		await SetTextAsync(source.Replace("ARM", "gr"));
		Assert.Equal(["green", "red"], ItemsOf(await AskAsync("textDocument/completion", 14, 4)).Keys.Order());
		
		await SetTextAsync(source.Replace("ARM", "Color.green => 2"));
		Assert.Equal(["green", "red"], ItemsOf(await AskAsync("textDocument/completion", 17, 14)).Keys.Order());
		Assert.Equal(0, await ShutDownAsync());
	}
}