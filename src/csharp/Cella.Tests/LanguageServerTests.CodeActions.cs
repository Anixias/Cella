using System.Text.Json.Nodes;
using Cella.LanguageServer;
using Xunit;

namespace Cella.Tests;

public sealed partial class LanguageServerTests
{
	private const char Cursor = '‸';
	private bool isInitialized;
	
	private async Task<(string Text, JsonArray Actions)> CodeActionsAtAsync(string marked)
	{
		var offset = marked.IndexOf(Cursor);
		var text = marked.Remove(offset, 1);
		if (!isInitialized)
		{
			await InitializeAsync();
			isInitialized = true;
		}
		
		await SetTextAsync(text);
		var line = text[..offset].Count(static c => c == '\n');
		var character = offset - (text.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1);
		var position = new JsonObject { ["line"] = line, ["character"] = character };
		var id = await RequestAsync("textDocument/codeAction", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = SourceUri },
			["range"] = new JsonObject { ["start"] = position, ["end"] = position.DeepClone() },
			["context"] = new JsonObject { ["diagnostics"] = new JsonArray() }
		});
		
		return (text, (JsonArray)(await ReadResponseAsync(id))["result"]!);
	}
	
	private async Task<string> FixAsync(string marked, string title)
	{
		var (text, actions) = await CodeActionsAtAsync(marked);
		var action = actions.FirstOrDefault(action => (string)action!["title"]! == title);
		Assert.True(action is not null,
			$"No '{title}' among: {string.Join(", ", actions.Select(static action => (string)action!["title"]!))}");
		
		return Apply(text, (JsonArray)action["edit"]!["changes"]![SourceUri]!);
	}
	
	private async Task<Dictionary<string, string>> MoveAsync(string marked, string title)
	{
		var (text, actions) = await CodeActionsAtAsync(marked);
		var action = actions.FirstOrDefault(action => (string)action!["title"]! == title);
		Assert.True(action is not null,
			$"No '{title}' among: {string.Join(", ", actions.Select(static action => (string)action!["title"]!))}");
		
		var files = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var change in (JsonArray)action["edit"]!["documentChanges"]!)
		{
			var uri = (string?)change!["uri"] ?? (string)change["textDocument"]!["uri"]!;
			var path = Protocol.ToPath(uri);
			var name = Path.GetFileName(path);
			if ((string?)change["kind"] == "create")
			{
				files[name] = "";
				continue;
			}
			
			if (!files.TryGetValue(name, out var current))
				current = Protocol.PathComparer.Equals(path, SourcePath)
					? text
					: (await File.ReadAllTextAsync(path, Token)).ReplaceLineEndings("\n");
			
			files[name] = Apply(current, (JsonArray)change["edits"]!);
		}
		
		return files;
	}
	
	private async Task<IEnumerable<string>> ActionTitlesAsync(string marked) =>
		(await CodeActionsAtAsync(marked)).Actions.Select(static action => (string)action!["title"]!);
	
	private static string Apply(string text, JsonArray edits)
	{
		foreach (var edit in edits.Reverse())
		{
			var range = edit!["range"]!;
			var start = Protocol.ToOffset(text, (int)range["start"]!["line"]!, (int)range["start"]!["character"]!);
			var end = Protocol.ToOffset(text, (int)range["end"]!["line"]!, (int)range["end"]!["character"]!);
			text = text[..start] + (string)edit["newText"]! + text[end..];
		}
		
		return text;
	}
	
	[Fact]
	public async Task FixesMisspelledNames()
	{
		Assert.Equal("mod test\n\ncount: fun -> i32 => 3\n\nmain: fun -> i32 => count()\n",
			await FixAsync("mod test\n\ncount: fun -> i32 => 3\n\nmain: fun -> i32 => co‸utn()\n",
				"Change to 'count'"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ConvertsBetweenBodyForms()
	{
		const string expression = "mod test\n\ntwice: fun(x: i32) -> i32 => x * 2\n\nmain: fun -> i32 => twice(2)\n";
		const string block =
			"mod test\n\ntwice: fun(x: i32) -> i32\n{\n\tret x * 2\n}\n\nmain: fun -> i32 => twice(2)\n";
		
		Assert.Equal(block,
			await FixAsync(expression.Replace("twice: fun(x", "tw‸ice: fun(x"), "Convert to block body"));
		
		Assert.Equal(expression, await FixAsync(block.Replace("twice: fun(x", "tw‸ice: fun(x"),
			"Convert to expression body"));
		
		const string action = "mod test\n\nshow: fun(x: i32) => x + 1\n\nmain: fun -> i32 => 0\n";
		Assert.Equal("mod test\n\nshow: fun(x: i32)\n{\n\tx + 1\n}\n\nmain: fun -> i32 => 0\n",
			await FixAsync(action.Replace("show:", "sh‸ow:"), "Convert to block body"));
		
		Assert.DoesNotContain("Convert to expression body",
			await ActionTitlesAsync("mod test\n\nma‸in: fun -> i32\n{\n\tval x = 1\n\tret x\n}\n"));
		
		Assert.Equal(expression, await FixAsync(block.Replace("{\n\tret", "‸{\n\tret"), "Convert to expression body"));
		Assert.Equal(expression, await FixAsync(block.Replace("\tret x", "\tr‸et x"), "Convert to expression body"));
		Assert.Equal(expression, await FixAsync(block.Replace("2\n}", "2\n}‸"), "Convert to expression body"));
		Assert.DoesNotContain("Convert to expression body",
			await ActionTitlesAsync(block.Replace("ret x * 2", "ret x * ‸2")));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Theory]
	[InlineData("Sorted: trait req Ord + ‸Eq", "Sorted: trait req Ord", "Remove redundant 'Eq'")]
	[InlineData("texture: ‸req Texture", "texture: Texture", "Remove redundant 'req'")]
	[InlineData("within[T: ‸Eq + Ord]", "within[T: Ord]", "Remove redundant 'Eq'")]
	[InlineData("val same = ‸imm texture", "val same = texture", "Remove redundant 'imm'")]
	[InlineData("val double = ‸own fun", "val double = fun", "Remove redundant 'own'")]
	public async Task RemovesRedundantMarkers(string marked, string replacement, string title)
	{
		var source = (await File.ReadAllTextAsync(Path.Combine(GoldenTests.GetSourceDirectory(), "Cases", "run",
			"redundant_markers.ce"), Token)).ReplaceLineEndings("\n");
		
		var original = marked.Replace(Cursor.ToString(), "");
		Assert.Equal(source.Replace(original, replacement), await FixAsync(source.Replace(original, marked), title));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task RemovesRedundantImports()
	{
		await File.WriteAllTextAsync(ProjectPath,
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\nImports = [\"test.util.helper\"]\n", Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, "util.ce"),
			"mod test.util\n\nhelper: pub fun -> i32 => 1\n\nother: pub fun -> i32 => 2\n", Token);
		
		const string body = "\nmain: fun -> i32 => helper() + other()\n";
		Assert.Equal("mod test\n\nuse test.util.other\n" + body,
			await FixAsync("mod test\n\nuse test.util.[‸helper, other]\n" + body, "Remove redundant import"));
		
		Assert.Equal("mod test\n\nuse test.util.other\n" + body,
			await FixAsync("mod test\n\nuse test.util.‸helper\nuse test.util.other\n" + body,
				"Remove redundant import"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private async Task<IEnumerable<string>> FadedRangesAsync(string text)
	{
		await SetTextAsync(text);
		var published = await ReadDiagnosticsAsync(static diagnostics => diagnostics.Count > 0);
		return ((JsonArray)published["params"]!["diagnostics"]!)
			.Where(static diagnostic => diagnostic!["tags"] is JsonArray tags && tags.Any(static tag => (int)tag! == 1))
			.Select(static diagnostic =>
			{
				var range = diagnostic!["range"]!;
				return $"{(int)range["start"]!["line"]!}:{(int)range["start"]!["character"]!}-" +
				       $"{(int)range["end"]!["line"]!}:{(int)range["end"]!["character"]!}";
			})
			.Order(StringComparer.Ordinal)
			.ToList();
	}
	
	[Fact]
	public async Task MovesTypesToTheirOwnFiles()
	{
		await WriteMoveUtilAsync();
		await File.WriteAllTextAsync(Path.Combine(_directory, "measured.ce"),
			"mod test\n\nuse test.util.helper\n\ntwice: fun -> i32 => helper() * 2\n", Token);
		
		const string source =
			"mod test\n\nuse test.util.[helper, Meters]\n\n// A point.\nPoint: rec\n{\n\tx: Meters\n}\n\n" +
			"Measured: trait\n{\n\tmeasure: fun(imm self) -> Meters\n}\n\n" +
			"main: fun -> i32 => helper()\n";
		
		var moved = await MoveAsync(source.Replace("Point: rec", "Po‸int: rec"), "Move to 'point.ce'");
		Assert.Equal("mod test\n\nuse test.util.Meters\n\n// A point.\nPoint: rec\n{\n\tx: Meters\n}\n",
			moved["point.ce"]);
		
		Assert.Equal(source.Replace("// A point.\nPoint: rec\n{\n\tx: Meters\n}\n\n", ""), moved["main.ce"]);
		
		moved = await MoveAsync(source.Replace("Measured: trait", "Measured‸: trait"), "Move to 'measured.ce'");
		Assert.Equal("mod test\n\nuse test.util.[helper, Meters]\n\ntwice: fun -> i32 => helper() * 2\n\n" +
		             "Measured: trait\n{\n\tmeasure: fun(imm self) -> Meters\n}\n", moved["measured.ce"]);
		
		Assert.Equal(source.Replace("Measured: trait\n{\n\tmeasure: fun(imm self) -> Meters\n}\n\n", ""),
			moved["main.ce"]);
		
		const string single = "mod test\n\nuse test.util.[helper, Meters]\nuse test.util.other\n\n" +
		                      "Point: rec\n{\n\tx: Meters\n}\n\nmain: fun -> i32 => helper()\n";
		
		moved = await MoveAsync(single.Replace("Point: rec", "Po‸int: rec"), "Move to 'point.ce'");
		Assert.Equal("mod test\n\nuse test.util.helper\nuse test.util.other\n\nmain: fun -> i32 => helper()\n",
			moved["main.ce"]);
		
		Assert.DoesNotContain("Move to 'main.ce'",
			await ActionTitlesAsync("mod test\n\nMa‸in: rec\n{\n}\n\nmain: fun -> i32 => 0\n"));
		
		Assert.DoesNotContain("Move to 'secret.ce'", await ActionTitlesAsync(
			"mod test\n\nSec‸ret: pvt rec\n{\n\tvalue: i32\n}\n\nmain: fun -> i32 => Secret(1).value\n"));
		
		Assert.DoesNotContain("Move to 'holder.ce'", await ActionTitlesAsync(
			"mod test\n\nHid: pvt rec\n{\n}\n\nHol‸der: rec\n{\n\thid: Hid\n}\n\nmain: fun -> i32 => 0\n"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ResolvesMovesWhenPicked()
	{
		await WriteMoveUtilAsync();
		await InitializeAsync(new JsonObject
		{
			["textDocument"] = new JsonObject
			{
				["codeAction"] = new JsonObject
				{
					["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("edit") }
				}
			}
		});
		
		isInitialized = true;
		var (_, actions) = await CodeActionsAtAsync(
			"mod test\n\nuse test.util.Meters\n\nPo‸int: rec\n{\n\tx: Meters\n}\n\n" +
			"main: fun -> i32 => 0\n");
		
		var action = actions.Single(action => (string)action!["title"]! == "Move to 'point.ce'")!;
		Assert.Null(action["edit"]);
		
		var resolved =
			await ReadResponseAsync(await RequestAsync("codeAction/resolve", (JsonObject)action.DeepClone()));
		
		var changes = (JsonArray)resolved["result"]!["edit"]!["documentChanges"]!;
		var source = changes.Last(change => change!["textDocument"] is not null)!;
		Assert.Equal(2, ((JsonArray)source["edits"]!).Count);
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private Task WriteMoveUtilAsync() => File.WriteAllTextAsync(Path.Combine(_directory, "util.ce"),
		"mod test.util\n\nhelper: pub fun -> i32 => 1\n\nother: pub fun -> i32 => 2\n\n" +
		"Meters: pub rec\n{\n\tvalue: i32\n}\n", Token);
	
	[Fact]
	public async Task FadesAndRemovesUnusedImports()
	{
		await File.WriteAllTextAsync(Path.Combine(_directory, "util.ce"),
			"mod test.util\n\nhelper: pub fun -> i32 => 1\n\nother: pub fun -> i32 => 2\n", Token);
		
		const string body = "\nmain: fun -> i32 => helper()\n";
		Assert.Equal("mod test\n\nuse test.util.helper\n" + body,
			await FixAsync("mod test\n\nuse test.util.[helper, ‸other]\n" + body, "Remove unused import"));
		
		Assert.Equal("mod test\n\nuse test.util.helper\n" + body,
			await FixAsync("mod test\n\nuse test.util.helper\nuse test.util.‸other\n" + body,
				"Remove unused import"));
		
		Assert.Equal(["2:23-2:28"], await FadedRangesAsync("mod test\n\nuse test.util.[helper, other]\n" + body));
		Assert.Equal(["2:0-2:15"], await FadedRangesAsync("mod test\n\nuse test.util.*\n\nmain: fun -> i32 => 0\n"));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ChangesUnchangedVarsToVal()
	{
		Assert.Equal("mod test\n\nmain: fun -> i32\n{\n\tval x = 3\n\tret x\n}\n",
			await FixAsync("mod test\n\nmain: fun -> i32\n{\n\tvar ‸x = 3\n\tret x\n}\n", "Change to 'val'"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task RemovesUnreachableCode()
	{
		Assert.Equal("mod test\n\nmain: fun -> i32\n{\n\tret 1\n}\n",
			await FixAsync("mod test\n\nmain: fun -> i32\n{\n\tret 1\n\t‸ret 2\n}\n", "Remove unreachable code"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task AddsMissingCases()
	{
		const string shape = "mod test\n\nShape: enum\n{\n\tcircle(radius: f64)\n\tsquare\n\tdot\n}\n\n";
		Assert.Equal(
			shape + "count: fun(s: Shape) -> i32 => match s { square => 1, circle(_) => panic, dot => panic }\n",
			await FixAsync(shape + "count: fun(s: Shape) -> i32 => ‸match s { square => 1 }\n", "Add missing cases"));
		
		Assert.Equal(
			shape +
			"count: fun(s: Shape) -> i32 => match s\n{\n\tsquare => 1\n\tcircle(_) => panic\n\tdot => panic\n}\n",
			await FixAsync(shape + "count: fun(s: Shape) -> i32 => ‸match s\n{\n\tsquare => 1\n}\n",
				"Add missing cases"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ImplementsMissingMembers()
	{
		const string start = "mod test\n\nShape: trait\n{\n\tarea: fun(imm self) -> f64\n" +
		                     "\tscaled: fun(imm self, factor: f64) -> Self\n}\n\nSquare: rec\n{\n\tside: f64\n}\n\n";
		
		Assert.Equal(start + "Square: impl Shape\n{\n\tarea: fun(imm self) -> f64 => panic\n\t\n" +
		             "\tscaled: fun(imm self, factor: f64) -> Square => panic\n}\n",
			await FixAsync(start + "Square: impl ‸Shape\n{\n}\n", "Implement missing members"));
		
		Assert.Equal(start + "Square: impl Shape\n{\n\tarea: fun(imm self) -> f64 => self.side * self.side\n\t\n" +
		             "\tscaled: fun(imm self, factor: f64) -> Square => panic\n}\n",
			await FixAsync(start + "Square: impl ‸Shape\n{\n\tarea: fun(imm self) -> f64 => self.side * self.side\n}\n",
				"Implement missing members"));
		
		Assert.Equal(start + "Square: impl Shape\n{\n\tarea: fun(imm self) -> f64 => panic\n\t\n" +
		             "\tscaled: fun(imm self, factor: f64) -> Square => panic\n}\n",
			await FixAsync(start + "Square: impl ‸Shape\n", "Implement missing members"));
		
		Assert.Equal(start + "Square: impl Shape\n{\n\tarea: fun(imm self) -> f64 => panic\n\t\n" +
		             "\tscaled: fun(imm self, factor: f64) -> Square => panic\n}\n",
			await FixAsync(start + "Square: impl ‸Shape\n{\n\t\n}\n", "Implement missing members"));
		
		Assert.Equal(start + "Square: impl Shape\n{\n\tarea: fun(imm self) -> f64 => self.side * self.side\n\t\n" +
		             "\tscaled: fun(imm self, factor: f64) -> Square => panic\n}\n",
			await FixAsync(
				start + "Square: impl ‸Shape\n{\n\tarea: fun(imm self) -> f64 => self.side * self.side\n\t\n\n}\n",
				"Implement missing members"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ImplementsMissingConstantsAndImpliedMembers()
	{
		const string bounded =
			"mod test\n\nBounded: trait req copy\n{\n\ttop: const Self\n\tclamp: fun(self) -> Self\n}\n\n";
		
		Assert.Equal(
			bounded + "i32: impl Bounded\n{\n\ttop: const i32 = panic\n\t\n\tclamp: fun(self) -> i32 => panic\n}\n",
			await FixAsync(bounded + "i32: impl ‸Bounded\n{\n}\n", "Implement missing members"));
		
		const string numeric = "mod test\n\nOrdered: trait req copy\n{\n" +
		                       "\torder: fun(imm left: Self, imm right: Self) -> i32\n}\n\n" +
		                       "Numeric: trait req Ordered\n{\n\tzero: const Self\n}\n\n";
		
		Assert.Equal(numeric + "i32: impl Numeric\n{\n\tzero: const i32 = 0\n\t\n" +
		             "\torder: fun(imm left: i32, imm right: i32) -> i32 => panic\n}\n",
			await FixAsync(numeric + "i32: impl ‸Numeric\n{\n\tzero: const i32 = 0\n}\n",
				"Implement missing members"));
		
		const string limits = "mod test\n\nLimits: trait req copy\n{\n\tlow: const Self\n\thigh: const Self\n" +
		                      "\tmiddle: const Self\n}\n\n";
		
		Assert.Equal(limits + "i32: impl Limits\n{\n\tlow: const i32 = 0\n\thigh: const i32 = panic\n" +
		             "\tmiddle: const i32 = panic\n}\n",
			await FixAsync(limits + "i32: impl ‸Limits\n{\n\tlow: const i32 = 0\n}\n", "Implement missing members"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task ImportsNamesThatAreNotFound()
	{
		await File.WriteAllTextAsync(Path.Combine(_directory, "util.ce"),
			"mod test.util\n\nhelper: pub fun -> i32 => 1\n\nShape: pub rec\n{\n\tside: i32\n}\n", Token);
		
		Assert.Equal("mod test\n\nuse test.util.helper\n\nmain: fun -> i32 => helper()\n",
			await FixAsync("mod test\n\nmain: fun -> i32 => he‸lper()\n", "Import 'test.util.helper'"));
		
		Assert.Equal(
			"mod test\n\nuse test.util.[helper, Shape]\n\nmain: fun -> i32 => helper()\n\nmake: fun -> Shape => Shape(1)\n",
			await FixAsync(
				"mod test\n\nuse test.util.helper\n\nmain: fun -> i32 => helper()\n\nmake: fun -> Sh‸ape => Shape(1)\n",
				"Import 'test.util.Shape'"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task FixesArgumentModes()
	{
		const string bump = "mod test\n\nbump: fun(mut x: i32) => x += 1\n\nmain: fun -> i32\n{\n\tvar n = 1\n";
		Assert.Equal(bump + "\tbump(mut n)\n\tret n\n}\n",
			await FixAsync(bump + "\tbump(‸n)\n\tret n\n}\n", "Add 'mut'"));
		
		const string show = "mod test\n\nshow: fun(x: i32) -> i32 => x\n\nmain: fun -> i32\n{\n\tvar n = 1\n\tn = 2\n";
		Assert.Equal(show + "\tret show(n)\n}\n", await FixAsync(show + "\tret show(‸mut n)\n}\n", "Remove 'mut'"));
		Assert.Equal(0, await ShutDownAsync());
	}
	
	private async Task<JsonObject> RequestAtCursorAsync(string method, string marked, JsonObject? extra = null)
	{
		var offset = marked.IndexOf(Cursor);
		var text = marked.Remove(offset, 1);
		if (!isInitialized)
		{
			await InitializeAsync();
			isInitialized = true;
		}
		
		await SetTextAsync(text);
		var parameters = new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = SourceUri },
			["position"] = new JsonObject
			{
				["line"] = text[..offset].Count(static c => c == '\n'),
				["character"] = offset - (text.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1)
			}
		};
		
		foreach (var (key, value) in extra ?? [])
			parameters[key] = value?.DeepClone();
		
		return await ReadResponseAsync(await RequestAsync(method, parameters));
	}
	
	[Fact]
	public async Task DescribesAndEditsLints()
	{
		await File.WriteAllTextAsync(ProjectPath,
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\n\n[Lints]\nNeverChanged = \"Off\"\n", Token);
		
		await InitializeAsync();
		var projectUri = UriOf(ProjectPath);
		var described = await ReadResponseAsync(await RequestAsync("cella/projectSettings", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = projectUri }
		}));
		
		var settings = (JsonObject)described["result"]!;
		Assert.Equal("Off", (string?)settings["lintLevels"]!["NeverChanged"]);
		Assert.Contains((JsonArray)settings["lints"]!, lint => (string?)lint!["name"] == "UnreachableCode" &&
		                                                       (string?)lint["default"] == "Warning");
		
		var edited = await ReadResponseAsync(await RequestAsync("cella/editProject", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = projectUri },
			["edits"] = new JsonArray(
				new JsonObject { ["lint"] = "NeverChanged", ["value"] = "Error" },
				new JsonObject { ["lint"] = "FloatPattern", ["condition"] = "release", ["value"] = "Off" })
		}));
		
		var edit = Assert.Single((JsonArray)edited["result"]!["edits"]!)!;
		var text = Apply(await File.ReadAllTextAsync(ProjectPath, Token), new JsonArray(edit.DeepClone()));
		Assert.Equal(
			"OutputType = \"Executable\"\nStandardLibrary = \"None\"\n\n[Lints]\nNeverChanged = \"Error\"\n\n" +
			"[When.release.Lints]\nFloatPattern = \"Off\"\n", text);
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task RenamesNamesThatBreakNamingRules()
	{
		Assert.Equal("mod test\n\nmakeValue: fun -> i32 => 1\n\nmain: fun -> i32 => makeValue()\n",
			await FixAsync("mod test\n\nmake_‸value: fun -> i32 => 1\n\nmain: fun -> i32 => make_value()\n",
				"Rename to 'makeValue'"));
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task RenamesSymbolsEverywhere()
	{
		const string source =
			"mod test\n\ncount: fun(start: i32) -> i32 => start + 1\n\nmain: fun -> i32 => co‸unt(1) + count(2)\n";
		
		var prepared = (await RequestAtCursorAsync("textDocument/prepareRename", source))["result"]!;
		Assert.Equal("count", (string)prepared["placeholder"]!);
		
		var renamed =
			await RequestAtCursorAsync("textDocument/rename", source, new JsonObject { ["newName"] = "total" });
		
		var edits = (JsonArray)renamed["result"]!["changes"]![SourceUri]!;
		Assert.Equal(
			"mod test\n\ntotal: fun(start: i32) -> i32 => start + 1\n\nmain: fun -> i32 => total(1) + total(2)\n",
			Apply(source.Replace("‸", ""), edits));
		
		var invalid = await RequestAtCursorAsync("textDocument/rename", source, new JsonObject { ["newName"] = "ret" });
		Assert.Equal("'ret' is a keyword", (string)invalid["error"]!["message"]!);
		
		Assert.Null((await RequestAtCursorAsync("textDocument/prepareRename",
			"mod test\n\nmain: fun -> i3‸2 => 1 + 2\n"))["result"]);
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Theory]
	[InlineData("mod test\n\nBuffer: rec\n{\n\tsize: usize\n\n\tlength: get(self) -> usize => self.size\n\n" +
	            "\tcount: prop usize\n\t{\n\t\tget(self) => self.size\n\t\tset(mut self, value) => self.size = value\n\t}\n}\n\n" +
	            "main: fun -> i32\n{\n\tvar buffer = Buffer(3)\n\tbuffer.count = 4\n\tret i32(buffer.length + buffer.count)\n}\n",
		"6:1:6 property declaration accessor|8:1:5 property declaration accessor|18:8:5 property accessor|" +
		"19:16:6 property accessor|19:32:5 property accessor")]
	[InlineData("mod test\n\ncheck: fun(start: i32, finish: i32)\n{\n\tif start > finish\n}\n",
		"4:4:5 parameter|4:12:6 parameter")]
	[InlineData("mod test\n\ncheck: fun(start: i32, finish: i32)\n{\n\twhile start > finish\n\tval x =\n}\n",
		"4:7:5 parameter|4:15:6 parameter")]
	[InlineData("mod test\n\nView: rec\n{\n\tlength: usize\n\n\tslice: fun(self, start: usize,\n\n" +
	            "\tfirst: fun(self) -> usize => self.length\n}\n",
		"2:0:4 struct declaration|6:1:5 method declaration|6:18:5 parameter declaration|8:1:5 method declaration")]
	[InlineData("mod test\n\nView: rec\n{\n\tlength: usize\n\n\tslice: fun(self) -> View[\n\n" +
	            "\tfirst: fun(self) -> usize => self.length\n}\n",
		"6:1:5 method declaration|8:1:5 method declaration")]
	[InlineData("mod test\n\nslice:\n\nhelper: fun(start: i32) -> i32 => start\n", "4:0:6 function declaration")]
	[InlineData(
		"mod test\n\nsame: fun(left: str) -> bool\n{\n\tif (left.byteLength !=-)\n\t\tret false\n\tret true\n}\n",
		"4:5:4 parameter|4:10:10 property accessor")]
	[InlineData("mod test\n\nsame: fun(left: array[i32, 2]) -> bool\n{\n\tif left[]\n\t\tret false\n\tif left[] !\n" +
	            "\t\tret false\n\tret true\n}\n",
		"4:4:4 parameter|6:4:4 parameter")]
	[InlineData("mod test\n\nBuffer-: rec\n{\n\tid: i32\n}\n\nsize: fun(b: Buffer) -> i32 => b.id\n",
		"2:0:6 struct declaration|7:13:6 struct|7:33:2 property")]
	[InlineData("mod test\n\nBits: union\n{\n\traw: u32\n\tvalue: f32\n}\n\nminimum: const f32 = Bits\n",
		"8:21:4 struct")]
	[InlineData("mod test.\n\nhelper: fun(start: i32) -> i32 => start\n",
		"2:0:6 function declaration|2:12:5 parameter declaration")]
	[InlineData("mod test\n\nPoint: rec\n{\n\tx: i32\n\n\tsum: fun(self) -> i32 => self.\n\n" +
	            "\tdouble: fun(self) -> i32 => self.x * 2\n}\n",
		"8:1:6 method declaration|8:34:1 property")]
	[InlineData("mod test\n\nfirst: fun -> i32 => second().\n\nsecond: fun -> i32 => 1\n",
		"2:21:6 function|4:0:6 function declaration")]
	[InlineData("mod test\n\nmain: fun -> i32\n{\n\tvar count = 1\n\tval\n\tcount = 2\n\tret count\n}\n",
		"6:1:5 variable|7:5:5 variable")]
	[InlineData("mod test\n\nshow: fun(a: i32, b: i32) => a\n\nmain: fun -> i32\n{\n\tval a = 1\n" +
	            "\tshow(\"\n\tshow(a, a)\n\tret 0\n}\n",
		"8:1:4 function|8:6:1 variable readonly")]
	[InlineData("mod test\n\nColor: enum\n{\n\tred\n\tgreen\n}\n\npick: fun(c: Color) -> i32\n{\n\tmatch c\n" +
	            "\t{\n\t\tred\n\t\tgreen => ret 2\n\t}\n\tret 0\n}\n",
		"10:7:1 parameter|13:2:5 enumMember")]
	[InlineData("mod test\n\nShape: enum\n{\n\tdot\n\tcircle(radius: f64)\n}\n\n" +
	            "size: fun(s: Shape) -> f64 => match s\n{\n\tdot => 0.0\n\tcircle(val r) => r\n}\n",
		"5:8:6 property declaration|10:1:3 enumMember|11:1:6 enumMember")]
	[InlineData("mod test\n\nBuffer: rec\n{\n\tsize: usize\n}.\n\nhelper: fun(b: Buffer) -> usize => b.size\n",
		"2:0:6 struct declaration|7:37:4 property")]
	[InlineData("mod test\n\nmain: fun -> i32 +\n{\n\tval count = 1\n\tret count\n}\n",
		"4:5:5 variable declaration readonly|5:5:5 variable readonly")]
	[InlineData("mod test\n\nOrd: trait req copy\n{\n\tcompare\n\t<: op(imm left: Self, imm right: Self) -> bool\n" +
	            "\t[]: op(self, index: usize) -> bool\n}\n",
		"5:11:4 parameter declaration|6:14:5 parameter declaration")]
	[InlineData("mod test\n\nShape: enum\n{\n\tcircle\n}\n\nmatching: fun(shape: Shape)\n{\n\tmatch shape\n}\n",
		"9:7:5 parameter")]
	public async Task ClassifiesNamesInUnfinishedCode(string source, string expected)
	{
		var tokens = await ClassifyAsync(source);
		foreach (var token in expected.Split('|'))
			Assert.Contains(token, tokens);
		
		Assert.Equal(0, await ShutDownAsync());
	}
	
	[Fact]
	public async Task LeavesUnknownTypesUnclassified()
	{
		var tokens = await ClassifyAsync("mod test\n\nmake: fun -> Cursor[i32] => 0\n");
		Assert.DoesNotContain(tokens, static token => token.StartsWith("2:13:", StringComparison.Ordinal));
		Assert.Equal(0, await ShutDownAsync());
	}
}