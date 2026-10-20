using System.Text.Json.Nodes;
using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.LanguageServer;

public static class Completion
{
	public const string Placeholder = "cellaCompletionProbe";
	
	private const int PlainText = 1;
	private const int Snippet = 2;
	
	private enum ItemKind
	{
		Method = 2,
		Function = 3,
		Field = 5,
		Variable = 6,
		Class = 7,
		Interface = 8,
		Module = 9,
		Property = 10,
		Enum = 13,
		Keyword = 14,
		EnumMember = 20,
		Constant = 21,
		Struct = 22,
		TypeParameter = 25
	}
	
	private static readonly string[] _statementKeywords =
	[
		"val", "var", "const", "ret", "if", "else", "for", "loop", "while", "do", "break", "cont", "match", "when",
		"atomic", "panic", "drop"
	];
	
	private static readonly string[] _expressionKeywords =
	[
		"true", "false", "null", "undef", "default", "new", "self", "imm", "mut", "own", "if", "match", "sizeOf",
		"alignOf", "nameOf"
	];
	
	private static readonly string[] _typeKeywords = ["dyn", "fun", "imm", "mut", ..NativeSymbols.GenericNames];
	
	private static readonly string[] _declarationKeywords =
	[
		"fun", "rec", "enum", "union", "trait", "impl", "ref", "const", "val", "var", "ext", "pub", "pvt", "mod", "op",
		"prop", "get", "set", "req"
	];
	
	private static readonly string[] _topLevelKeywords = ["mod", "use", "ext", "pub", "pvt"];
	
	public static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
	
	public static int FindNameStart(string text, int offset)
	{
		var start = offset;
		while (start > 0 && IsNameCharacter(text[start - 1]))
			start--;
		
		return start;
	}
	
	public static JsonObject ToList(CompletionProbe? probe)
	{
		var items = new JsonArray();
		IEnumerable<string> keywords = (probe?.Place, probe?.Site) switch
		{
			(CompletionPlace.Name, CompletionSite.Statement) => _statementKeywords.Concat(_expressionKeywords)
				.Distinct(),
			(CompletionPlace.Name, _) => _expressionKeywords,
			(CompletionPlace.Type, _) => _typeKeywords,
			(CompletionPlace.Member, _) => [],
			(_, CompletionSite.Declaration) => _declarationKeywords,
			(_, CompletionSite.TopLevel) => _topLevelKeywords,
			_ => []
		};
		
		if (probe is not null)
		{
			foreach (var group in probe.Candidates.Where(static candidate => IsListed(NameOf(candidate)))
				         .GroupBy(static candidate => (NameOf(candidate), IsFunction(candidate))))
				items.Add(group.Key.Item2
					? ToFunctionItem(probe, group.Key.Item1, [..group])
					: ToItem(probe, group.First()));
		}
		
		var labels = items.Select(static item => (string)item!["label"]!).ToHashSet();
		foreach (var keyword in keywords.Where(labels.Add))
			items.Add(new JsonObject
			{
				["label"] = keyword,
				["kind"] = (int)ItemKind.Keyword,
				["sortText"] = $"3{keyword}"
			});
		
		return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
	}
	
	public static JsonObject? ToSignatureHelp(CompletionCall call)
	{
		var activeParameter = call.Argument;
		var functions = call.Candidates
			.Where(candidate => IsFunction(candidate) && NameOf(candidate) == call.Callee)
			.Select(static candidate => candidate.Function!.Value)
			.ToList();
		
		if (functions.Count == 0)
			return null;
		
		var signatures = new JsonArray();
		var activeSignature = -1;
		foreach (var function in functions)
		{
			var (label, ranges) = SemanticModel.DescribeSignature(function, true);
			if (activeSignature < 0 && ranges.Length > activeParameter)
				activeSignature = signatures.Count;
			
			signatures.Add(new JsonObject
			{
				["label"] = label,
				["parameters"] = new JsonArray([
					..ranges.Select(static range => new JsonObject
					{
						["label"] = new JsonArray(range.Start, range.End)
					})
				])
			});
		}
		
		return new JsonObject
		{
			["signatures"] = signatures,
			["activeSignature"] = Math.Max(activeSignature, 0),
			["activeParameter"] = activeParameter
		};
	}
	
	private static string NameOf(CompletionCandidate candidate) => candidate.Symbol switch
	{
		FunctionSymbol function => function.DeclaredName,
		var symbol => symbol.Name
	};
	
	private static bool IsFunction(CompletionCandidate candidate) =>
		candidate is { Function: not null, Symbol: FunctionSymbol or MethodSymbol };
	
	private static bool IsListed(string name) =>
		name is [var first, ..] && (char.IsLetter(first) || first == '_') && name != "_" &&
		name.All(IsNameCharacter) && !name.Contains(Placeholder) && TokenTypeInfo.GetKeyword(name) is null;
	
	private static JsonObject ToItem(CompletionProbe probe, CompletionCandidate candidate)
	{
		var name = NameOf(candidate);
		return new JsonObject
		{
			["label"] = name,
			["kind"] = (int)KindOf(candidate.Symbol),
			["detail"] = SemanticModel.Describe(candidate),
			["labelDetails"] = new JsonObject { ["detail"] = SemanticModel.DescribeTail(candidate) },
			["sortText"] = $"{RankOf(probe, candidate)}{name}",
			["insertText"] = name,
			["insertTextFormat"] = PlainText
		};
	}
	
	private static JsonObject ToFunctionItem(CompletionProbe probe, string name, List<CompletionCandidate> overloads)
	{
		var functions = overloads.Select(static overload => overload.Function!.Value).ToList();
		var detail = SemanticModel.Describe(overloads[0]);
		var labelDetails = new JsonObject { ["detail"] = SemanticModel.DescribeTail(overloads[0]) };
		if (overloads.Count > 1)
		{
			var others = overloads.Count == 2 ? "+1 overload" : $"+{overloads.Count - 1} overloads";
			detail += $" ({others})";
			labelDetails["description"] = others;
		}
		
		var item = new JsonObject
		{
			["label"] = name,
			["kind"] = (int)(overloads.Any(static overload => overload.Symbol is MethodSymbol)
				? ItemKind.Method
				: ItemKind.Function),
			["detail"] = detail,
			["labelDetails"] = labelDetails,
			["sortText"] = $"{overloads.Min(overload => RankOf(probe, overload))}{name}"
		};
		
		if (overloads.Count > 1)
			item["documentation"] = new JsonObject
			{
				["kind"] = "markdown",
				["value"] = $"```cella\n{string.Join('\n', overloads.Select(SemanticModel.Describe))}\n```"
			};
		
		if (probe.ExpectedType is FunctionType)
		{
			item["insertText"] = name;
			item["insertTextFormat"] = PlainText;
			return item;
		}
		
		var modes = functions.Select(function => SemanticModel.GetCallParameters(function)
			.Select(i => ModeOf(function.Signature.GetMode(i))).ToArray()).ToList();
		
		var shared = modes.Skip(1).All(other => other.SequenceEqual(modes[0])) ? modes[0] : null;
		item["insertText"] = shared switch
		{
			null => $"{name}($0)",
			[] => $"{name}()$0",
			_ => $"{name}({string.Join(", ", shared.Select(static (mode, i) => $"{mode}${{{i + 1}}}"))})$0"
		};
		
		item["insertTextFormat"] = Snippet;
		return item;
	}
	
	private static string ModeOf(ParameterMode mode) => mode switch
	{
		ParameterMode.Mut => "mut ",
		ParameterMode.Own => "own ",
		ParameterMode.Imm => "imm ",
		_ => ""
	};
	
	private static int RankOf(CompletionProbe probe, CompletionCandidate candidate)
	{
		var type = candidate switch
		{
			{ Function: { } function } => function.Signature.ReturnType,
			{ Type: { } declared } => declared,
			{ Symbol: LocalVariableSymbol local } => local.Type,
			_ => null
		};
		
		if (probe.ExpectedType is { } expected && type == expected ||
		    probe.Place == CompletionPlace.Member && candidate.Symbol is ModulePathSymbol)
			return 0;
		
		return candidate.Symbol is LocalVariableSymbol or ParameterSymbol or FieldSymbol or PropertySymbol ? 1 : 2;
	}
	
	private static ItemKind KindOf(Symbol symbol) => symbol switch
	{
		LocalVariableSymbol { IsConstant: true } => ItemKind.Constant,
		GlobalSymbol { IsConstant: true } => ItemKind.Constant,
		LocalVariableSymbol or GlobalSymbol or ParameterSymbol => ItemKind.Variable,
		FieldSymbol => ItemKind.Field,
		PropertySymbol => ItemKind.Property,
		EnumCaseSymbol => ItemKind.EnumMember,
		TraitSymbol or TraitType => ItemKind.Interface,
		TypeParameterSymbol => ItemKind.TypeParameter,
		TypeSymbol { OriginalDefinition: RecordSymbol { IsRef: true } } => ItemKind.Struct,
		TypeSymbol { OriginalDefinition: RecordSymbol } => ItemKind.Class,
		TypeSymbol { OriginalDefinition: EnumSymbol } => ItemKind.Enum,
		ModulePathSymbol => ItemKind.Module,
		MethodSymbol => ItemKind.Method,
		FunctionSymbol => ItemKind.Function,
		_ => ItemKind.Keyword
	};
}