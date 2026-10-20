using System.IO.Pipes;
using System.Text.Json.Nodes;
using Cella.LanguageServer;
using Xunit;

namespace Cella.Tests;

public sealed class LanguageServerTests : IAsyncDisposable
{
	private const string ValidSource = "mod test\n\nmain: fun -> i32 => 0\n";
	private const string InvalidSource = "mod test\n\nmain: fun -> i32 => true\n";
	
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
		File.WriteAllText(Path.Combine(_directory, "test.celp"),
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\n");
		
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
	
	private string SourceUri => UriOf(SourcePath);
	
	private static string UriOf(string path) => OperatingSystem.IsWindows()
		? $"file:///{char.ToLowerInvariant(path[0])}%3A{path[2..].Replace('\\', '/')}"
		: new Uri(path).AbsoluteUri;
	
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
	
	private async Task<JsonObject> InitializeAsync(JsonObject? clientCapabilities = null)
	{
		var id = await RequestAsync("initialize", new JsonObject
		{
			["rootUri"] = new Uri(_directory).AbsoluteUri,
			["capabilities"] = clientCapabilities ?? new JsonObject()
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
		await OpenSourceAsync(InvalidSource);
		
		var published = await ReadDiagnosticsAsync(static diagnostics => diagnostics.Count > 0);
		var error = Assert.Single((JsonArray)published["params"]!["diagnostics"]!)!;
		Assert.Equal(1, (int)error["severity"]!);
		Assert.Equal(2, (int)error["range"]!["start"]!["line"]!);
		Assert.Equal(20, (int)error["range"]!["start"]!["character"]!);
		Assert.Equal(24, (int)error["range"]!["end"]!["character"]!);
		
		await NotifyAsync("textDocument/didChange", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = SourceUri, ["version"] = 2 },
			["contentChanges"] = new JsonArray(new JsonObject { ["text"] = ValidSource })
		});
		
		await ReadDiagnosticsAsync(static diagnostics => diagnostics.Count == 0);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private string ProjectPath => Path.Combine(_directory, "test.celp");
	
	private async Task OpenSourceAsync(string text)
	{
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
	}
	
	private async Task<JsonNode> ReadProjectDiagnosticAsync()
	{
		var published = await ReadUntilAsync(message =>
			(string?)message["method"] == "textDocument/publishDiagnostics" &&
			Protocol.PathComparer.Equals(Protocol.ToPath((string)message["params"]!["uri"]!), ProjectPath));
		
		return Assert.Single((JsonArray)published["params"]!["diagnostics"]!)!;
	}
	
	[Fact]
	public async Task PublishesProjectErrorsAndKeepsChecking()
	{
		await File.WriteAllTextAsync(ProjectPath,
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\nColour = \"blue\"\n", Token);
		
		await InitializeAsync();
		await OpenSourceAsync(InvalidSource);
		
		var error = await ReadProjectDiagnosticAsync();
		Assert.Equal("Unknown setting 'Colour'", (string?)error["message"]);
		Assert.Equal(1, (int)error["severity"]!);
		Assert.Equal(0, (int)error["range"]!["start"]!["line"]!);
		Assert.Equal(0, (int)error["range"]!["start"]!["character"]!);
		Assert.Equal(25, (int)error["range"]!["end"]!["character"]!);
		
		await ReadDiagnosticsAsync(static diagnostics => diagnostics.Count == 1);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task PublishesProjectSyntaxErrorsOnTheirLine()
	{
		await File.WriteAllTextAsync(ProjectPath,
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\n  Colour =\n", Token);
		
		await InitializeAsync();
		await OpenSourceAsync(InvalidSource);
		
		var error = await ReadProjectDiagnosticAsync();
		Assert.Equal(2, (int)error["range"]!["start"]!["line"]!);
		Assert.Equal(2, (int)error["range"]!["start"]!["character"]!);
		Assert.Equal(10, (int)error["range"]!["end"]!["character"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task DescribesAndEditsProjects()
	{
		var library = Path.Combine(_directory, "geometry", "geometry.celp");
		Directory.CreateDirectory(Path.GetDirectoryName(library)!);
		await File.WriteAllTextAsync(library, "OutputType = \"StaticLibrary\"\n", Token);
		await File.WriteAllTextAsync(ProjectPath,
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\n\n[When.release]\nBoundsChecks = false\n", Token);
		
		var capabilities = await InitializeAsync();
		Assert.True((bool)capabilities["experimental"]!["projects"]!);
		
		var listed = await ReadResponseAsync(await RequestAsync("cella/projects", new JsonObject()));
		var projects = (JsonArray)listed["result"]!;
		Assert.Equal(new[] { "geometry", "test" }, projects.Select(static project => (string)project!["name"]!));
		Assert.Equal("StaticLibrary", (string?)projects[0]!["outputType"]);
		
		var projectUri = UriOf(ProjectPath);
		var added = await ReadResponseAsync(await RequestAsync("cella/editProject", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = projectUri },
			["edits"] = new JsonArray(
				new JsonObject { ["addReference"] = UriOf(library) },
				new JsonObject { ["setting"] = "DebugInfo", ["condition"] = "release", ["value"] = false })
		}));
		
		var edit = Assert.Single((JsonArray)added["result"]!["edits"]!)!;
		Assert.Equal(5, (int)edit["range"]!["start"]!["line"]!);
		Assert.Equal("DebugInfo = false\n\n[[ProjectReferences]]\nPath = 'geometry/geometry.celp'\n",
			(string?)edit["newText"]);
		
		var described = await ReadResponseAsync(await RequestAsync("cella/projectSettings", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = projectUri },
			["text"] = "OutputType = \"Executable\"\n\n[When.release]\nBoundsChecks = false\n"
		}));
		
		var settings = (JsonObject)described["result"]!;
		Assert.Equal("Executable", (string?)settings["values"]!["OutputType"]);
		var release = Assert.Single((JsonArray)settings["conditions"]!)!;
		Assert.Equal("release", (string?)release["condition"]);
		Assert.False((bool)release["values"]!["BoundsChecks"]!);
		Assert.Contains((JsonArray)settings["settings"]!, static setting => (string?)setting!["name"] == "OutputType" &&
		                                                                    (string?)setting["kind"] == "choice");
		
		var refused = await ReadResponseAsync(await RequestAsync("cella/editProject", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = projectUri },
			["edits"] = new JsonArray(new JsonObject
			{
				["setting"] = "StandardLibrary",
				["condition"] = "release",
				["value"] = "Core"
			})
		}));
		
		Assert.Equal("Can't set 'StandardLibrary' here", (string?)refused["error"]!["message"]);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private const string NavigationSource = """
	                                        mod test
	                                        
	                                        Point: rec
	                                        {
	                                        	x: i32
	                                        	y: i32
	                                        }
	                                        
	                                        sum: fun(imm point: Point) -> i32 => point.x + point.y
	                                        
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
	
	private async Task<string> HoverAsync(int line, int character) =>
		(string)(await AskAsync("textDocument/hover", line, character))!["contents"]!["value"]!;
	
	private async Task<JsonNode?> DefinitionAsync(int line, int character) =>
		(await AskAsync("textDocument/definition", line, character))?[0];
	
	private static string Code(string text) => $"```cella\n{text}\n```";
	
	[Fact]
	public async Task AnswersHoverAndDefinition()
	{
		await File.WriteAllTextAsync(SourcePath, NavigationSource, Token);
		await InitializeAsync();
		
		Assert.Equal(Code("sum: fun(imm point: Point) -> i32"), await HoverAsync(13, 6));
		Assert.Equal(Code("val origin: Point"), await HoverAsync(13, 14));
		Assert.Equal(Code("x: i32"), await HoverAsync(8, 43));
		Assert.Equal(Code("Point: rec"), await HoverAsync(8, 21));
		Assert.Equal(Code("Point: rec"), await HoverAsync(12, 15));
		
		var function = await DefinitionAsync(13, 6);
		Assert.Equal(8, (int)function!["range"]!["start"]!["line"]!);
		Assert.Equal(0, (int)function["range"]!["start"]!["character"]!);
		Assert.Equal(3, (int)function["range"]!["end"]!["character"]!);
		
		var type = await DefinitionAsync(12, 15);
		Assert.Equal(2, (int)type!["range"]!["start"]!["line"]!);
		
		var field = await DefinitionAsync(8, 43);
		Assert.Equal(4, (int)field!["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)field["range"]!["start"]!["character"]!);
		
		Assert.Null(await DefinitionAsync(8, 30));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task HoversInPlainTextWhenAsked()
	{
		await File.WriteAllTextAsync(SourcePath, NavigationSource, Token);
		await InitializeAsync(new JsonObject
		{
			["textDocument"] = new JsonObject
			{
				["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("plaintext") }
			}
		});
		
		var contents = (await AskAsync("textDocument/hover", 13, 14))!["contents"]!;
		Assert.Equal("plaintext", (string)contents["kind"]!);
		Assert.Equal("val origin: Point", (string)contents["value"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task LinksTypeParametersInInlinedQuestionBodies()
	{
		await File.WriteAllTextAsync(SourcePath, """
		                                         mod test
		                                         
		                                         Sized[T]: enum
		                                         {
		                                         	held(value: T)
		                                         
		                                         	?: op(own self) -> usize => sizeOf(T)
		                                         }
		                                         
		                                         first: fun -> usize => Sized.held(40i64)?
		                                         
		                                         main: fun -> i32 => 0
		                                         
		                                         """, Token);
		
		await InitializeAsync();
		Assert.Equal(Code("T"), await HoverAsync(6, 36));
		var definition = await DefinitionAsync(6, 36);
		Assert.Equal(2, (int)definition!["range"]!["start"]!["line"]!);
		Assert.Equal(6, (int)definition["range"]!["start"]!["character"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task NamesValuesInPatterns()
	{
		await File.WriteAllTextAsync(SourcePath, """
		                                         mod test
		                                         
		                                         Point: rec
		                                         {
		                                         	x: i32
		                                         
		                                         	==: op(a: Point, b: Point) -> bool => a.x == b.x
		                                         }
		                                         
		                                         Origin: val Point = Point(0)
		                                         
		                                         where: fun(p: Point) -> i32 => match p
		                                         {
		                                         	Origin => 0
		                                         	else => 1
		                                         }
		                                         
		                                         pick: fun(n: i32, wanted: i32) -> i32 => match n
		                                         {
		                                         	wanted => 1
		                                         	else => 2
		                                         }
		                                         
		                                         main: fun -> i32 => 0
		                                         """, Token);
		
		await InitializeAsync();
		Assert.Equal(Code("Origin: val Point"), await HoverAsync(13, 1));
		Assert.Equal(9, (int)(await DefinitionAsync(13, 1))!["range"]!["start"]!["line"]!);
		Assert.Equal(Code("wanted: i32"), await HoverAsync(19, 1));
		var parameter = await DefinitionAsync(19, 1);
		Assert.Equal(17, (int)parameter!["range"]!["start"]!["line"]!);
		Assert.Equal(18, (int)parameter["range"]!["start"]!["character"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task NamesQuestionOperatorUses()
	{
		Assert.Contains("12:6:1 method", await ClassifyAsync("""
		                                                     mod test
		                                                     
		                                                     Holder[T: copy]: rec
		                                                     {
		                                                     	t: T
		                                                     
		                                                     	?: op(own self) -> T => self.t
		                                                     }
		                                                     
		                                                     main: fun -> i32
		                                                     {
		                                                     	val h = Holder(5)
		                                                     	ret h?
		                                                     }
		                                                     """));
		
		Assert.Equal(Code("?: op(own self) -> i32"), await HoverAsync(12, 6));
		var declaration = await DefinitionAsync(12, 6);
		Assert.Equal(6, (int)declaration!["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)declaration["range"]!["start"]!["character"]!);
		Assert.Equal(["12:6"], await UsagesAsync(6, 1, false));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private async Task<List<string>> UsagesAsync(int line, int character, bool includeDeclaration)
	{
		var parameters = Position(line, character);
		parameters["textDocument"]!["uri"] = SourceUri;
		parameters["context"] = new JsonObject { ["includeDeclaration"] = includeDeclaration };
		var usages = (JsonArray)(await ReadResponseAsync(await RequestAsync("textDocument/references", parameters)))
			["result"]!;
		
		return usages.Select(static usage =>
		{
			Assert.EndsWith("main.ce", (string)usage!["uri"]!);
			var start = usage["range"]!["start"]!;
			return $"{(int)start["line"]!}:{(int)start["character"]!}";
		}).ToList();
	}
	
	[Fact]
	public async Task ListsRunnableFunctions()
	{
		await InitializeAsync();
		await OpenSourceAsync("mod test\n\nmain: fun -> i32 => 0\n\ndemo: pub fun\n{\n}\n\nhelper: fun\n{\n}\n\n" +
		                      "scaled: pub fun(factor: i32) -> i32 => factor\n\nflag: pub fun -> bool => true\n");
		
		var id = await RequestAsync("cella/runnables",
			new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = SourceUri } });
		
		var runnables = (JsonArray)(await ReadResponseAsync(id))["result"]!;
		Assert.Equal(["test.main main 2:0", "test.demo 4:0"], runnables.Select(static runnable =>
			$"{(string)runnable!["name"]!}{((bool)runnable["main"]! ? " main" : "")} " +
			$"{(int)runnable["range"]!["start"]!["line"]!}:{(int)runnable["range"]!["start"]!["character"]!}"));
	}
	
	[Fact]
	public async Task FindsUsages()
	{
		await File.WriteAllTextAsync(SourcePath, NavigationSource, Token);
		await InitializeAsync();
		
		Assert.Equal(["8:20", "12:14"], await UsagesAsync(2, 2, false));
		Assert.Equal(["2:0", "8:20", "12:14"], await UsagesAsync(12, 16, true));
		Assert.Equal(["8:37", "8:47"], await UsagesAsync(8, 14, false));
		Assert.Equal(["8:43"], await UsagesAsync(4, 1, false));
		Assert.Equal(["13:5"], await UsagesAsync(8, 0, false));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task LinksArgumentsOfFailedCalls()
	{
		await File.WriteAllTextAsync(SourcePath, """
		                                         mod test
		                                         
		                                         y: fun(a: i32) {}
		                                         y: fun(a: bool) {}
		                                         
		                                         Color: enum { red, green }
		                                         
		                                         P: rec
		                                         {
		                                         	x: i32
		                                         }
		                                         
		                                         main: fun -> i32
		                                         {
		                                         	val name = 2.5
		                                         	y(name)
		                                         	val p = P(1)
		                                         	p.missing(name)
		                                         	Color.nope(name)
		                                         	ret 0
		                                         }
		                                         
		                                         """, Token);
		
		await InitializeAsync();
		Assert.Equal(Code("val name: f64"), await HoverAsync(15, 3));
		var declaration = await DefinitionAsync(15, 3);
		Assert.Equal(14, (int)declaration!["range"]!["start"]!["line"]!);
		Assert.Equal(5, (int)declaration["range"]!["start"]!["character"]!);
		Assert.Equal(Code("val p: P"), await HoverAsync(17, 1));
		Assert.Equal(Code("val name: f64"), await HoverAsync(17, 11));
		Assert.Equal(Code("val name: f64"), await HoverAsync(18, 12));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesNamesInTheOpenText()
	{
		await File.WriteAllTextAsync(SourcePath, NavigationSource, Token);
		Assert.Equal(
		[
			"0:0:3 keyword",
			"0:4:4 namespace",
			"2:0:5 struct declaration",
			"2:7:3 keyword",
			"4:1:1 property declaration",
			"4:4:3 keyword defaultLibrary",
			"5:1:1 property declaration",
			"5:4:3 keyword defaultLibrary",
			"8:0:5 function declaration",
			"8:15:5 parameter declaration",
			"8:22:5 struct",
			"8:32:3 keyword defaultLibrary",
			"8:39:5 parameter",
			"8:45:1 property",
			"8:49:5 parameter",
			"8:55:1 property",
			"10:0:4 function declaration",
			"10:13:3 keyword defaultLibrary",
			"12:5:6 variable declaration readonly",
			"12:14:5 struct",
			"13:5:5 function",
			"13:15:6 variable readonly"
		], await ClassifyAsync(NavigationSource.Replace("sum", "total")));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private const string ModulePathSource = """
	                                        mod test
	                                        
	                                        use game.geometry.area
	                                        use game.geometry.[Shape, limit]
	                                        
	                                        k: fun(z: i32) -> i32
	                                        {
	                                        	ret area(limit) + z.max
	                                        }
	                                        
	                                        """;
	
	private async Task WriteGeometryAsync() =>
		await File.WriteAllTextAsync(Path.Combine(_directory, "geometry.ce"), """
		                                                                      mod game.geometry
		                                                                      
		                                                                      area: pub fun(side: i32) -> i32 => side * side
		                                                                      Shape: pub rec
		                                                                      {
		                                                                      	side: i32
		                                                                      }
		                                                                      
		                                                                      limit: pub val i32 = 3
		                                                                      
		                                                                      """, Token);
	
	[Fact]
	public async Task ClassifiesModulePathsAndImports()
	{
		await WriteGeometryAsync();
		await File.WriteAllTextAsync(SourcePath, ModulePathSource, Token);
		var tokens = await ClassifyAsync(ModulePathSource);
		Assert.Contains("0:4:4 namespace", tokens);
		Assert.Contains("2:4:4 namespace", tokens);
		Assert.Contains("2:9:8 namespace", tokens);
		Assert.Contains("2:18:4 function", tokens);
		Assert.Contains("3:19:5 struct", tokens);
		Assert.Contains("3:26:5 variable readonly static", tokens);
		Assert.Contains("7:5:4 function", tokens);
		Assert.Contains("7:10:5 variable readonly static", tokens);
		Assert.Contains("7:19:1 parameter", tokens);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task HoversModulePathsAndImports()
	{
		await WriteGeometryAsync();
		await File.WriteAllTextAsync(SourcePath, ModulePathSource, Token);
		await InitializeAsync();
		Assert.Equal(Code("mod test"), await HoverAsync(0, 5));
		Assert.Equal(Code("mod game"), await HoverAsync(2, 5));
		Assert.Equal(Code("mod game.geometry"), await HoverAsync(2, 12));
		Assert.Equal(Code("area: fun(side: i32) -> i32"), await HoverAsync(2, 20));
		Assert.Equal(Code("Shape: rec"), await HoverAsync(3, 21));
		Assert.Equal(Code("limit: val i32"), await HoverAsync(3, 28));
		Assert.Equal(Code("z: i32"), await HoverAsync(7, 19));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesTypesWithBrokenOrMissingBodies()
	{
		var tokens = await ClassifyAsync("""
		                                 mod test
		                                 
		                                 A: rec
		                                 {
		                                 	x: i32
		                                 	
		                                 	+: op(imm a: A, imm b: A) -> A = a
		                                 }
		                                 
		                                 B: rec
		                                 
		                                 main: fun -> i32 => A(1).x
		                                 
		                                 """);
		
		Assert.Contains("2:0:1 struct declaration", tokens);
		Assert.Contains("4:1:1 property declaration", tokens);
		Assert.Contains("9:0:1 struct declaration", tokens);
		Assert.Contains("11:20:1 struct", tokens);
		Assert.Contains("11:25:1 property", tokens);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesOperatorsConstantsAndTypeArguments()
	{
		Assert.Equal(
		[
			"0:0:3 keyword",
			"0:4:4 namespace",
			"2:0:3 struct declaration",
			"2:4:1 typeParameter declaration",
			"2:7:4 keyword",
			"2:14:3 keyword",
			"4:1:1 property declaration",
			"4:4:1 typeParameter",
			"5:1:1 method declaration",
			"5:4:2 keyword",
			"5:11:1 parameter declaration",
			"5:14:3 struct",
			"5:18:1 typeParameter",
			"5:26:1 parameter declaration",
			"5:29:3 struct",
			"5:33:1 typeParameter",
			"5:40:1 typeParameter",
			"5:45:1 parameter",
			"5:47:1 property",
			"8:0:5 variable declaration readonly static",
			"8:13:3 keyword defaultLibrary",
			"10:0:4 function declaration",
			"10:13:3 keyword defaultLibrary",
			"12:7:5 variable declaration readonly static",
			"13:5:4 variable declaration",
			"13:11:5 keyword defaultLibrary",
			"13:17:4 keyword defaultLibrary",
			"14:5:1 variable declaration readonly",
			"14:9:3 struct",
			"14:13:3 keyword defaultLibrary",
			"14:18:3 keyword defaultLibrary",
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
		                       	+: op(imm a: Vec[T], imm b: Vec[T]) -> T => a.x
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
		Assert.Equal(Code("const count: i32 = 3"), await HoverAsync(14, 22));
		Assert.Equal(Code("char"), await HoverAsync(13, 17));
		Assert.Equal(["15:11"], await UsagesAsync(5, 1, false));
		
		var operation = await DefinitionAsync(15, 11);
		Assert.Equal(5, (int)operation!["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)operation["range"]!["start"]!["character"]!);
		
		var parameter = await DefinitionAsync(4, 4);
		Assert.Equal(2, (int)parameter!["range"]!["start"]!["line"]!);
		Assert.Equal(4, (int)parameter["range"]!["start"]!["character"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private async Task<List<string>> ClassifyAsync(string text, string? uri = null, string languageId = "cella")
	{
		uri ??= SourceUri;
		var legend = (await InitializeAsync())["semanticTokensProvider"]!["legend"]!;
		var types = ((JsonArray)legend["tokenTypes"]!).Select(static type => (string)type!).ToArray();
		var modifiers = ((JsonArray)legend["tokenModifiers"]!).Select(static modifier => (string)modifier!).ToArray();
		
		await NotifyAsync("textDocument/didOpen", new JsonObject
		{
			["textDocument"] = new JsonObject
			{
				["uri"] = uri,
				["languageId"] = languageId,
				["version"] = 1,
				["text"] = text
			}
		});
		
		var id = await RequestAsync("textDocument/semanticTokens/full",
			new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
		
		var data = ((JsonArray)(await ReadResponseAsync(id))["result"]!["data"]!).Select(static n => (int)n!).ToArray();
		var (line, character) = (0, 0);
		var tokens = new List<string>();
		for (var i = 0; i < data.Length; i += 5)
		{
			character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
			line += data[i];
			var flags = data[i + 4];
			var applied = modifiers.Where((_, bit) => (flags & (1 << bit)) != 0);
			tokens.Add(string.Join(' ', [$"{line}:{character}:{data[i + 2]}", types[data[i + 3]], ..applied]));
		}
		
		return tokens;
	}
	
	[Fact]
	public async Task ClassifiesNamesTheScannerAccepts()
	{
		var tokens = await ClassifyAsync("""
		                                 mod test
		                                 
		                                 main: fun -> i32
		                                 {
		                                 	val a‿b = 3
		                                 	val Ⅻ = 12
		                                 	val use = 4
		                                 	ret a‿b + Ⅻ + use
		                                 }
		                                 
		                                 """);
		
		Assert.Contains("4:5:3 variable declaration readonly", tokens);
		Assert.Contains("5:5:1 variable declaration readonly", tokens);
		Assert.Contains("6:5:3 variable declaration readonly", tokens);
		Assert.Contains("7:5:3 variable readonly", tokens);
		Assert.Contains("7:11:1 variable readonly", tokens);
		Assert.Contains("7:15:3 variable readonly", tokens);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesContextualKeywordsTheParserReads()
	{
		var tokens = await ClassifyAsync("""
		                                 mod test
		                                 
		                                 pub ext
		                                 {
		                                 	puts: fun(text: cstr) -> i32
		                                 }
		                                 
		                                 Meters: rec
		                                 {
		                                 	value: i32
		                                 
		                                 	as: op(self) -> i32 => self.value
		                                 
		                                 	doubled: prop i32
		                                 	{
		                                 		get(self) => self.value * 2
		                                 		pvt set(mut self, value)
		                                 		{
		                                 			self.value = value / 2
		                                 		}
		                                 	}
		                                 }
		                                 
		                                 Show: trait
		                                 {
		                                 	show: fun(imm self) -> i32
		                                 }
		                                 
		                                 Meters: impl Show
		                                 {
		                                 	show: fun(imm self) -> i32 => self.value
		                                 }
		                                 
		                                 main: fun -> i32
		                                 {
		                                 	val set = Meters(3)
		                                 	puts("hi")
		                                 	ret i32(set)
		                                 }
		                                 
		                                 mod: fun -> i32 => 2
		                                 
		                                 """);
		
		Assert.Equal(
		[
			"0:0:3 keyword", "2:0:3 keyword", "2:4:3 keyword", "7:8:3 keyword", "11:1:2 keyword", "11:5:2 keyword",
			"13:10:4 keyword", "15:2:3 keyword", "16:2:3 keyword", "16:6:3 keyword", "23:6:5 keyword",
			"28:8:4 keyword"
		], tokens.Where(static token => token.EndsWith(" keyword")));
		
		Assert.Contains("4:17:4 keyword defaultLibrary", tokens);
		Assert.Contains("37:5:3 keyword defaultLibrary", tokens);
		Assert.Contains("35:5:3 variable declaration readonly", tokens);
		Assert.Contains("37:9:3 variable readonly", tokens);
		Assert.Contains("40:0:3 function declaration", tokens);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesProjectSettingsByTable()
	{
		const string project = """"
		                       OutputType = "Executable"
		                       "CellaVersion" = "0.1.0"
		                       Path = "x"
		                       Flags = { Path = true, When = false }
		                       Notes = """
		                       OutputType = "no"
		                       """
		                       ProjectReferences = [{ Path = "../lib", Alias = "l" }]
		                       
		                       [When.release]
		                       DebugInfo = false
		                       Flags = { fast = true }
		                       
		                       [When."debug && windows".Flags]
		                       Path = true
		                       
		                       [[ProjectReferences]]
		                       Path = "../a"
		                       
		                       [[When.debug.ProjectReferences]]
		                       Alias = "b"
		                       
		                       """";
		
		var lines = project.Split('\n');
		var settings = (await ClassifyAsync(project, UriOf(ProjectPath), "cella-project"))
			.Select(static token => token.Split(' ', ':'))
			.Select(parts => lines[int.Parse(parts[0])].Substring(int.Parse(parts[1]), int.Parse(parts[2])));
		
		Assert.Equal(
		[
			"OutputType", "\"CellaVersion\"", "Flags", "ProjectReferences", "Path", "Alias", "When", "DebugInfo",
			"Flags", "When", "Flags", "ProjectReferences", "Path", "When", "ProjectReferences", "Alias"
		], settings);
		
		Assert.Equal(0, await ShutDownAsync());
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
		                                 
		                                 	*: op(imm self) -> imm[Point] => self.inner
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
		                                 
		                                 Handle: rec
		                                 {
		                                 	id: i32
		                                 
		                                 	drop: op { }
		                                 }
		                                 
		                                 Owner: rec
		                                 {
		                                 	handle: Handle
		                                 }
		                                 
		                                 """);
		
		Assert.Contains("2:0:5 struct declaration", tokens);
		Assert.Contains("14:0:4 struct declaration ref", tokens);
		Assert.Contains("34:0:6 struct declaration drop", tokens);
		Assert.Contains("41:0:5 struct declaration drop", tokens);
		Assert.Contains("43:9:6 struct drop", tokens);
		Assert.Contains("19:0:5 enum declaration", tokens);
		Assert.Contains("31:5:1 variable readonly", tokens);
		Assert.Contains("31:6:1 method", tokens);
		Assert.Contains("31:11:1 variable readonly", tokens);
		Assert.DoesNotContain(tokens, static token => token.StartsWith("31:12:1 "));
		
		Assert.Equal(Code("*: op(imm self) -> imm[Point]"), await HoverAsync(31, 6));
		Assert.Equal(Code("val b: Box"), await HoverAsync(31, 5));
		Assert.Equal(Code("View: ref rec"), await HoverAsync(29, 9));
		
		var dereference = await DefinitionAsync(31, 6);
		Assert.Equal(11, (int)dereference!["range"]!["start"]!["line"]!);
		Assert.Equal(1, (int)dereference["range"]!["start"]!["character"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task HoversVariablesAsDeclared()
	{
		const string source = """
		                      mod test
		                      
		                      Point: rec
		                      {
		                      	x: i32
		                      }
		                      
		                      Box: rec
		                      {
		                      	inner: Point
		                      
		                      	*: op(imm self) -> imm[Point] => self.inner
		                      }
		                      
		                      g: fun(x: imm[Point], mut y: Point, imm z: Point) -> i32 => x.x + y.x + z.x
		                      
		                      main: fun -> i32
		                      {
		                      	var points = [Point(1), Point(2)]
		                      	for imm point in points
		                      	{
		                      		val c = Box(point)
		                      		val n = *c
		                      		val f = fun(w: imm[Point]) -> i32 => w.x
		                      	}
		                      
		                      	for mut point in points
		                      		point.x = point.x + 1
		                      
		                      	ret 0
		                      }
		                      
		                      """;
		
		await File.WriteAllTextAsync(SourcePath, source, Token);
		await InitializeAsync();
		Assert.Equal(Code("mut y: Point"), await HoverAsync(14, 66));
		Assert.Equal(Code("imm z: Point"), await HoverAsync(14, 72));
		Assert.Equal(Code("val point: imm[Point]"), await HoverAsync(19, 10));
		Assert.Equal(Code("val point: imm[Point]"), await HoverAsync(21, 15));
		Assert.Equal(Code("val c: Box"), await HoverAsync(22, 11));
		Assert.Equal(Code("*: op(imm self) -> imm[Point]"), await HoverAsync(22, 10));
		Assert.Equal(Code("w: imm[Point]"), await HoverAsync(23, 14));
		Assert.Equal(Code("val point: mut[Point]"), await HoverAsync(27, 3));
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
	                                        
	                                        	length: fun(imm self) -> i32 => self.x
	                                        	shift: fun(mut self, by: i32) => self.x = self.x + by
	                                        }
	                                        
	                                        scale: fun(mut target: Point, factor: i32) => target.x = target.x * factor
	                                        scale: fun(mut target: Point, imm by: Point) => target.x = target.x * by.x
	                                        move: fun(mut target: Point, imm offset: Point) => target.x = offset.x
	                                        twice: fun(value: i32) -> i32 => value * 2
	                                        apply: fun(f: fun[i32 -> i32], value: i32) -> i32 => f(value)
	                                        
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
		Assert.Equal(": var Point", (string)names["p"]["labelDetails"]!["detail"]!);
		Assert.Equal("+1 overload", (string)names["scale"]["labelDetails"]!["description"]!);
		Assert.StartsWith(": fun(", (string)names["scale"]["labelDetails"]!["detail"]!);
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
		
		var looped = ItemsOf(await AskAtCursorAsync("textDocument/completion",
			"while p.x < 2 // match retries\n\t{\n\t\tp.|\n\t}"));
		
		Assert.Equal(["length", "shift", "x"], looped.Keys.Order());
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task OffersKeywordsByPlace()
	{
		await InitializeAsync();
		Assert.Contains("ret", ItemsOf(await AskAtCursorAsync("textDocument/completion", "|")).Keys);
		
		var expression = ItemsOf(await AskAtCursorAsync("textDocument/completion", "val r = |"));
		Assert.Contains("true", expression.Keys);
		Assert.DoesNotContain("ret", expression.Keys);
		
		await SetTextAsync("mod test\n\nhelper: \n\nmain: fun -> i32 => 0\n");
		var declaration = ItemsOf(await AskAsync("textDocument/completion", 2, 8));
		Assert.Contains("fun", declaration.Keys);
		Assert.Contains("pub", declaration.Keys);
		Assert.DoesNotContain("use", declaration.Keys);
		
		await SetTextAsync("mod test\n\n\n\nmain: fun -> i32 => 0\n");
		var topLevel = ItemsOf(await AskAsync("textDocument/completion", 2, 0));
		Assert.Contains("use", topLevel.Keys);
		Assert.DoesNotContain("ret", topLevel.Keys);
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
		
		var commented = await AskAtCursorAsync("textDocument/signatureHelp", "scale(/* ), */ mut p, |)");
		Assert.Equal(1, (int)commented!["activeParameter"]!);
		
		var method = await AskAtCursorAsync("textDocument/signatureHelp", "p.shift(|)");
		var signature = Assert.Single((JsonArray)method!["signatures"]!)!;
		Assert.Equal("shift: fun(by: i32)", (string)signature["label"]!);
		Assert.Equal(11, (int)signature["parameters"]![0]!["label"]![0]!);
		
		var unclosed = await AskAtCursorAsync("textDocument/signatureHelp", "scale(mut p, |");
		Assert.Equal(1, (int)unclosed!["activeParameter"]!);
		Assert.Equal(2, ((JsonArray)unclosed["signatures"]!).Count);
		
		var nested = await AskAtCursorAsync("textDocument/signatureHelp", "scale(mut p, twice(|))");
		var inner = Assert.Single((JsonArray)nested!["signatures"]!)!;
		Assert.Equal("twice: fun(value: i32) -> i32", (string)inner["label"]!);
		Assert.Equal(0, (int)nested["activeParameter"]!);
		
		Assert.Null(await AskAtCursorAsync("textDocument/signatureHelp", "scale(mut p, 1)|"));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task NamesTheGlobalsADebuggerStopCanSee()
	{
		await File.WriteAllTextAsync(Path.Combine(_directory, "app.ce"), """
		                                                                 mod app
		                                                                 
		                                                                 counter: pub var i32 = 1
		                                                                 hidden: var i32 = 2
		                                                                 
		                                                                 Owner: pub rec
		                                                                 {
		                                                                 	value: i32
		                                                                 	made: var i32 = 0
		                                                                 }
		                                                                 
		                                                                 """, Token);
		
		await File.WriteAllTextAsync(SourcePath, """
		                                         mod test
		                                         
		                                         use app.counter
		                                         use app.Owner
		                                         
		                                         local: var i32 = 4
		                                         
		                                         main: fun -> i32
		                                         {
		                                         	val before = counter + local
		                                         	ret before + Owner.made
		                                         }
		                                         
		                                         peek: fun(owner: Owner) -> i32 => owner.value
		                                         
		                                         """, Token);
		
		await InitializeAsync();
		var visible = ((JsonArray)(await AskAsync("cella/visibleGlobals", 9, 0))!)
			.Select(static global => $"{global!["name"]} {global["debugName"]}")
			.Order(StringComparer.Ordinal);
		
		Assert.Equal(["counter app::counter", "local test::local"], visible);
		var made = await AskAsync("cella/valueAt", 10, 20);
		Assert.Equal("app::Owner::made", (string?)made?["global"]?["debugName"]);
		Assert.Equal(20, (int)made!["range"]!["start"]!["character"]!);
		Assert.Equal("app::counter", (string?)(await AskAsync("cella/valueAt", 9, 14))?["global"]?["debugName"]);
		
		var before = await AskAsync("cella/valueAt", 10, 6);
		Assert.Null(before?["global"]);
		Assert.Equal(5, (int)before!["range"]!["start"]!["character"]!);
		Assert.Null(await AskAsync("cella/valueAt", 10, 15));
		Assert.Equal(34, (int)(await AskAsync("cella/valueAt", 13, 40))!["range"]!["start"]!["character"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task CompletesUsePaths()
	{
		await File.WriteAllTextAsync(Path.Combine(_directory, "physics.ce"), """
		                                                                     mod game.physics
		                                                                     
		                                                                     step: pub fun -> i32 => 1
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
			var text = $"mod test\n\nuse {path}\n\nmain: fun -> i32 => 0\n";
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
	
	private const string InactiveSource = """
	                                      mod test
	                                      
	                                      trace: fun(id: i32) when release
	                                      {
	                                      }
	                                      
	                                      main: fun -> i32
	                                      {
	                                      	when debug
	                                      	{
	                                      		val a = 1
	                                      	}
	                                      	else
	                                      	{
	                                      		val b = 2
	                                      	}
	                                      	
	                                      	trace(4)
	                                      	ret match 1
	                                      	{
	                                      		when release
	                                      		{
	                                      			1 => 2
	                                      		}
	                                      		else => 3
	                                      	}
	                                      }
	                                      
	                                      """;
	
	private static List<string> InactiveRanges(JsonArray diagnostics) =>
	[
		..diagnostics
			.Where(static diagnostic => diagnostic!["tags"] is JsonArray tags && tags.Any(static tag => (int)tag! == 1))
			.Select(static diagnostic =>
			{
				var range = diagnostic!["range"]!;
				return $"{(int)range["start"]!["line"]!}:{(int)range["start"]!["character"]!}-" +
				       $"{(int)range["end"]!["line"]!}:{(int)range["end"]!["character"]!}";
			})
			.Order(StringComparer.Ordinal)
	];
	
	private async Task ReadInactiveAsync(params (string Uri, string[] Expected)[] files)
	{
		var pending = files.ToList();
		await ReadUntilAsync(message =>
		{
			if ((string?)message["method"] != "textDocument/publishDiagnostics")
				return false;
			
			var path = Protocol.ToPath((string)message["params"]!["uri"]!);
			var ranges = InactiveRanges((JsonArray)message["params"]!["diagnostics"]!);
			pending.RemoveAll(file => Protocol.PathComparer.Equals(Protocol.ToPath(file.Uri), path) &&
			                          ranges.SequenceEqual(file.Expected));
			
			return pending.Count == 0;
		});
	}
	
	[Fact]
	public async Task DimsInactiveCode()
	{
		await File.WriteAllTextAsync(SourcePath, InactiveSource, Token);
		await InitializeAsync();
		await ReadInactiveAsync((SourceUri, ["13:1-15:2", "17:1-17:9", "21:2-23:3"]));
		
		await NotifyAsync("cella/configuration", new JsonObject { ["release"] = true });
		await ReadInactiveAsync((SourceUri, ["9:1-11:2"]));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task DimsInactiveCodeAfterAMove()
	{
		const string source = "mod test\n\nwhen linux\n{\n\tseparator: val char = '/'\n}\n\nmain: fun -> i32 => 0\n";
		await File.WriteAllTextAsync(SourcePath, source, Token);
		await InitializeAsync();
		await OpenSourceAsync(source);
		await ReadInactiveAsync((SourceUri, ["3:0-5:1"]));
		
		var movedPath = Path.Combine(_directory, "moved", "main.ce");
		Directory.CreateDirectory(Path.GetDirectoryName(movedPath)!);
		File.Move(SourcePath, movedPath);
		var movedUri = UriOf(movedPath);
		
		await NotifyAsync("textDocument/didClose",
			new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = SourceUri } });
		
		await NotifyAsync("workspace/didChangeWatchedFiles", new JsonObject
		{
			["changes"] = new JsonArray(new JsonObject { ["uri"] = SourceUri, ["type"] = 2 })
		});
		
		await NotifyAsync("textDocument/didOpen", new JsonObject
		{
			["textDocument"] = new JsonObject
			{
				["uri"] = movedUri,
				["languageId"] = "cella",
				["version"] = 1,
				["text"] = source
			}
		});
		
		await ReadInactiveAsync((movedUri, ["3:0-5:1"]), (SourceUri, []));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ChecksTheConfiguredFlagsAndTarget()
	{
		await File.WriteAllTextAsync(ProjectPath,
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\n\n[Flags]\nlogging = false\n", Token);
		
		var otherPath = Path.Combine(_directory, "other.ce");
		await File.WriteAllTextAsync(otherPath, "mod test.other\nuse when logging\n\nvalue: fun -> i32 => 1\n", Token);
		await File.WriteAllTextAsync(SourcePath,
			"mod test\n\nwhen linux\n{\n\tseparator: val char = '/'\n}\n\nmain: fun -> i32 => 0\n", Token);
		
		await InitializeAsync();
		var otherUri = UriOf(otherPath);
		await ReadInactiveAsync((otherUri, ["3:0-3:22"]), (SourceUri, ["3:0-5:1"]));
		
		await NotifyAsync("cella/configuration", new JsonObject
		{
			["target"] = "linux-x64",
			["flags"] = new JsonObject { ["logging"] = true }
		});
		
		await ReadInactiveAsync((otherUri, []), (SourceUri, []));
		
		await NotifyAsync("cella/configuration", new JsonObject());
		await ReadInactiveAsync((otherUri, ["3:0-3:22"]), (SourceUri, ["3:0-5:1"]));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task StartsWithTheGivenConfiguration()
	{
		await File.WriteAllTextAsync(SourcePath, InactiveSource, Token);
		var id = await RequestAsync("initialize", new JsonObject
		{
			["rootUri"] = new Uri(_directory).AbsoluteUri,
			["capabilities"] = new JsonObject(),
			["initializationOptions"] = new JsonObject
			{
				["configuration"] = new JsonObject { ["release"] = true }
			}
		});
		
		await ReadResponseAsync(id);
		await NotifyAsync("initialized", new JsonObject());
		await ReadInactiveAsync((SourceUri, ["9:1-11:2"]));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private const string TraitConstantSource = """
	                                           mod test
	                                           
	                                           Number: trait req copy
	                                           {
	                                           	maxValue: const Self
	                                           	bits: const u32
	                                           	bytes: const u32 = bits / 8
	                                           }
	                                           
	                                           i32: impl Number
	                                           {
	                                           	maxValue: const i32 = 0x7FFFFFFF
	                                           	bits: const u32 = 32
	                                           }
	                                           
	                                           Wide: rec impl Number
	                                           {
	                                           	x: i32
	                                           	
	                                           	maxValue: const Wide = Wide(i32.maxValue)
	                                           	bits: const u32 = 64
	                                           }
	                                           
	                                           i64: impl
	                                           {
	                                           	limit: const i64 = 5
	                                           	twice: const i64 = limit * 2
	                                           }
	                                           
	                                           top[T: Number]: fun -> T => T.maxValue
	                                           
	                                           total: const u32 = i32.bytes + Wide.bits
	                                           
	                                           main: fun -> i32
	                                           {
	                                           	const scale = 0x10
	                                           	ret i32.maxValue - i32(i64.twice) - scale
	                                           }
	                                           
	                                           """;
	
	[Fact]
	public async Task ClassifiesTraitAndImplConstants()
	{
		var tokens = await ClassifyAsync(TraitConstantSource);
		Assert.Contains("4:1:8 variable declaration readonly static", tokens);
		Assert.Contains("5:1:4 variable declaration readonly static", tokens);
		Assert.Contains("6:1:5 variable declaration readonly static", tokens);
		Assert.Contains("6:20:4 variable readonly static", tokens);
		Assert.Contains("11:1:8 variable declaration readonly static", tokens);
		Assert.Contains("12:1:4 variable declaration readonly static", tokens);
		Assert.Contains("19:1:8 variable declaration readonly static", tokens);
		Assert.Contains("19:33:8 variable readonly static", tokens);
		Assert.Contains("20:1:4 variable declaration readonly static", tokens);
		Assert.Contains("25:1:5 variable declaration readonly static", tokens);
		Assert.Contains("26:1:5 variable declaration readonly static", tokens);
		Assert.Contains("26:20:5 variable readonly static", tokens);
		Assert.Contains("29:30:8 variable readonly static", tokens);
		Assert.Contains("31:23:5 variable readonly static", tokens);
		Assert.Contains("31:36:4 variable readonly static", tokens);
		Assert.Contains("36:9:8 variable readonly static", tokens);
		Assert.Contains("36:28:5 variable readonly static", tokens);
		Assert.Contains("35:7:5 variable declaration readonly static", tokens);
		Assert.Contains("36:37:5 variable readonly static", tokens);
		Assert.Equal(["31:23"], await UsagesAsync(6, 1, false));
		Assert.Equal(["26:20"], await UsagesAsync(25, 1, false));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task HoversConstantValues()
	{
		await File.WriteAllTextAsync(SourcePath, TraitConstantSource, Token);
		await InitializeAsync();
		Assert.Equal(Code("maxValue: const Self"), await HoverAsync(4, 2));
		Assert.Equal(Code("bytes: const u32 = bits / 8"), await HoverAsync(6, 2));
		Assert.Equal(Code("maxValue: const i32 = 0x7FFFFFFF\n= 2147483647"), await HoverAsync(11, 2));
		Assert.Equal(Code("bits: const u32 = 32"), await HoverAsync(12, 2));
		Assert.Equal(Code("maxValue: const Wide = Wide(i32.maxValue)\n= Wide(2147483647)"), await HoverAsync(19, 2));
		Assert.Equal(Code("twice: const i64 = limit * 2\n= 10"), await HoverAsync(26, 2));
		Assert.Equal(Code("maxValue: const T"), await HoverAsync(29, 32));
		Assert.Equal(Code("total: const u32 = i32.bytes + Wide.bits\n= 68"), await HoverAsync(31, 2));
		Assert.Equal(Code("bytes: const u32 = bits / 8\n= 4"), await HoverAsync(31, 25));
		Assert.Equal(Code("bits: const u32 = 64"), await HoverAsync(31, 38));
		Assert.Equal(Code("const scale: i32 = 0x10\n= 16"), await HoverAsync(36, 38));
		Assert.Equal(Code("maxValue: const i32 = 0x7FFFFFFF\n= 2147483647"), await HoverAsync(36, 11));
		Assert.Equal(Code("twice: const i64 = limit * 2\n= 10"), await HoverAsync(36, 29));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task KeepsImplicitSelfOutOfDeclarations()
	{
		await File.WriteAllTextAsync(SourcePath, """
		                                         mod test
		                                         
		                                         Box[T]: rec
		                                         {
		                                         	data: T
		                                         
		                                         	new: op(own value: T)
		                                         	{
		                                         		self.data = data
		                                         	}
		                                         }
		                                         
		                                         main: fun -> i32 => 0
		                                         
		                                         """, Token);
		
		await InitializeAsync();
		Assert.Null(await AskAsync("textDocument/hover", 8, 15));
		Assert.Null(await DefinitionAsync(8, 15));
		Assert.Null(await DefinitionAsync(8, 3));
		Assert.Empty(await UsagesAsync(8, 3, true));
		Assert.Equal(4, (int)(await DefinitionAsync(8, 8))!["range"]!["start"]!["line"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesOperatorOperandsAsParameters()
	{
		var tokens = await ClassifyAsync("""
		                                 mod test
		                                 
		                                 Number: trait req copy
		                                 {
		                                 	new: op(seed: i32)
		                                 	+: op(imm a: Self, imm b: Self) -> Self
		                                 	-: op(imm a: Self, imm b: Self) -> Self => a + b
		                                 }
		                                 
		                                 Point: rec
		                                 {
		                                 	x: i32
		                                 	
		                                 	new: op(value: i32)
		                                 	{
		                                 		self.x = value
		                                 	}
		                                 	
		                                 	==: op(imm a: Point, imm b: Point) -> bool => a.x == b.x
		                                 }
		                                 
		                                 Point: impl Number
		                                 {
		                                 	+: op(imm a: Point, imm b: Point) -> Point => Point(a.x + b.x)
		                                 }
		                                 
		                                 main: fun -> i32 => 0
		                                 
		                                 """);
		
		Assert.Contains("4:9:4 parameter declaration", tokens);
		Assert.Contains("5:11:1 parameter declaration", tokens);
		Assert.Contains("5:24:1 parameter declaration", tokens);
		Assert.Contains("6:44:1 parameter", tokens);
		Assert.Contains("13:9:5 parameter declaration", tokens);
		Assert.Contains("15:11:5 parameter", tokens);
		Assert.Contains("18:12:1 parameter declaration", tokens);
		Assert.Contains("18:47:1 parameter", tokens);
		Assert.Contains("23:11:1 parameter declaration", tokens);
		Assert.Contains("23:59:1 parameter", tokens);
		Assert.Equal(Code("imm a: Self"), await HoverAsync(5, 11));
		Assert.Equal(["15:11"], await UsagesAsync(13, 9, false));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesProjectImports()
	{
		const string project = "OutputType = \"Executable\"\nStandardLibrary = \"None\"\n" +
		                       "Imports = [\"game.geometry.area\", \"game.geometry.Shape\", \"game.geometry.*\", " +
		                       "\"game.nothing.x\"]\n";
		
		await WriteGeometryAsync();
		await File.WriteAllTextAsync(ProjectPath, project, Token);
		var tokens = await ClassifyAsync(project, UriOf(ProjectPath), "cella-project");
		Assert.Equal(
		[
			"0:0:10 property",
			"1:0:15 property",
			"2:0:7 property",
			"2:12:4 namespace",
			"2:17:8 namespace",
			"2:26:4 function",
			"2:34:4 namespace",
			"2:39:8 namespace",
			"2:48:5 struct",
			"2:57:4 namespace",
			"2:62:8 namespace",
			"2:76:4 namespace"
		], tokens);
		
		var parameters = Position(2, 27);
		parameters["textDocument"]!["uri"] = UriOf(ProjectPath);
		var hover = (await ReadResponseAsync(await RequestAsync("textDocument/hover", parameters)))["result"];
		Assert.Equal(Code("area: fun(side: i32) -> i32"), (string)hover!["contents"]!["value"]!);
		
		var definition =
			(await ReadResponseAsync(await RequestAsync("textDocument/definition", (JsonObject)parameters.DeepClone())))
			["result"]![0]!;
		
		Assert.EndsWith("geometry.ce", (string)definition["uri"]!);
		Assert.Equal(2, (int)definition["range"]!["start"]!["line"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesALibrarysOwnPrelude()
	{
		const string project = "OutputType = \"StaticLibrary\"\nStandardLibrary = \"None\"\n" +
		                       "Prelude = [\"std.process.panic\"]\n";
		
		await File.WriteAllTextAsync(ProjectPath, project, Token);
		await File.WriteAllTextAsync(Path.Combine(_directory, "panic.ce"),
			"mod std.process\n\npanic: pub op(message: str, location: str) -> never\n{\n\tloop\n\t{\n\t}\n}\n", Token);
		
		var tokens = await ClassifyAsync(project, UriOf(ProjectPath), "cella-project");
		Assert.Equal(
		[
			"0:0:10 property", "1:0:15 property", "2:0:7 property", "2:12:3 namespace", "2:16:7 namespace",
			"2:24:5 function"
		], tokens);
		
		var parameters = Position(2, 25);
		parameters["textDocument"]!["uri"] = UriOf(ProjectPath);
		var hover = (await ReadResponseAsync(await RequestAsync("textDocument/hover", parameters)))["result"];
		Assert.Equal(Code("panic: op(message: str, location: str) -> never"), (string)hover!["contents"]!["value"]!);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ClassifiesReferencedPreludes()
	{
		const string library = "OutputType = \"StaticLibrary\"\nStandardLibrary = \"None\"\n" +
		                       "Prelude = [\"lib.greet\", \"lib.gr\\u0065et\"]\n";
		
		var libraryDirectory = Path.Combine(_directory, "lib");
		var libraryPath = Path.Combine(libraryDirectory, "lib.celp");
		Directory.CreateDirectory(libraryDirectory);
		await File.WriteAllTextAsync(libraryPath, library, Token);
		await File.WriteAllTextAsync(Path.Combine(libraryDirectory, "lib.ce"),
			"mod lib\n\ngreet: pub fun -> i32 => 1\n", Token);
		
		await File.WriteAllTextAsync(ProjectPath,
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\n\n[[ProjectReferences]]\nPath = 'lib/lib.celp'\n",
			Token);
		
		var tokens = await ClassifyAsync(library, UriOf(libraryPath), "cella-project");
		Assert.Equal(["0:0:10 property", "1:0:15 property", "2:0:7 property", "2:12:3 namespace", "2:16:5 function"],
			tokens);
		
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
		var cases = ItemsOf(await AskAsync("textDocument/completion", 14, 4));
		Assert.Equal(["green", "red"], cases.Keys.Order());
		Assert.Equal("Color.green", (string)cases["green"]["detail"]!);
		Assert.Equal(": Color", (string)cases["green"]["labelDetails"]!["detail"]!);
		
		await SetTextAsync(source.Replace("ARM", "Color.green => 2"));
		Assert.Equal(["green", "red"], ItemsOf(await AskAsync("textDocument/completion", 17, 14)).Keys.Order());
		
		await SetTextAsync(source.Replace("Color.red => 1", "Color.red => \"}\".byteLength")
			.Replace("ARM", "Color.gr"));
		
		Assert.Equal(["green", "red"], ItemsOf(await AskAsync("textDocument/completion", 14, 10)).Keys.Order());
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task CompletesTraitNames()
	{
		const string source = """
		                      mod test
		                      
		                      Additive: pub trait
		                      {
		                      }
		                      
		                      Point: rec IMPL
		                      {
		                      }
		                      
		                      Number: pub trait req copy + REQ
		                      {
		                      	minValue: const Self
		                      }
		                      
		                      main: fun -> i32 => 0
		                      
		                      """;
		
		await InitializeAsync();
		await SetTextAsync(source.Replace("IMPL", "").Replace("REQ", "Add"));
		var required = ItemsOf(await AskAsync("textDocument/completion", 10, 32));
		Assert.Contains("Additive", required.Keys);
		Assert.DoesNotContain("Point", required.Keys);
		Assert.DoesNotContain("main", required.Keys);
		
		await SetTextAsync(source.Replace("IMPL", "").Replace("REQ", ""));
		Assert.Contains("Additive", ItemsOf(await AskAsync("textDocument/completion", 10, 29)).Keys);
		
		await SetTextAsync(source.Replace("IMPL", "impl Ad").Replace("REQ", "Additive"));
		var implemented = ItemsOf(await AskAsync("textDocument/completion", 6, 18));
		Assert.Contains("Additive", implemented.Keys);
		Assert.DoesNotContain("Point", implemented.Keys);
		Assert.Equal(0, await ShutDownAsync());
	}
}