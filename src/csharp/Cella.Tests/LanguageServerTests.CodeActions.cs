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
		
		Assert.Equal(start + "Square: impl Shape\n{\n\tarea: fun(imm self) -> f64 => panic\n\n" +
		             "\tscaled: fun(imm self, factor: f64) -> Square => panic\n}\n",
			await FixAsync(start + "Square: impl ‸Shape\n{\n}\n", "Implement missing members"));
		
		Assert.Equal(start + "Square: impl Shape\n{\n\tarea: fun(imm self) -> f64 => self.side * self.side\n\n" +
		             "\tscaled: fun(imm self, factor: f64) -> Square => panic\n}\n",
			await FixAsync(start + "Square: impl ‸Shape\n{\n\tarea: fun(imm self) -> f64 => self.side * self.side\n}\n",
				"Implement missing members"));
		
		Assert.Equal(start + "Square: impl Shape\n{\n\tarea: fun(imm self) -> f64 => panic\n\n" +
		             "\tscaled: fun(imm self, factor: f64) -> Square => panic\n}\n",
			await FixAsync(start + "Square: impl ‸Shape\n", "Implement missing members"));
		
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
		"6:1:6 property declaration|8:1:5 property declaration|18:8:5 property|19:16:6 property|19:32:5 property")]
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