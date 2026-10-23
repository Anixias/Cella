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
		Reference = 18,
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
	
	private static readonly HashSet<string> _completionKeywords =
		["ret", "if", "while", "match", "in", "is", "panic", "drop", "imm", "mut", "own", "break", "cont"];
	
	public static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
	
	public static bool FollowsCompletionKeyword(string text, int offset)
	{
		var end = offset;
		while (end > 0 && text[end - 1] is ' ' or '\t')
			end--;
		
		var start = FindNameStart(text, end);
		return end < offset && _completionKeywords.Contains(text[start..end]);
	}
	
	public static int FindNameStart(string text, int offset)
	{
		var start = offset;
		while (start > 0 && IsNameCharacter(text[start - 1]))
			start--;
		
		return start;
	}
	
	public static JsonObject ToList(CompletionProbe? probe, string text, int inserted)
	{
		var items = new JsonArray();
		IEnumerable<string> keywords = (probe?.Place, probe?.Site) switch
		{
			(CompletionPlace.Name, CompletionSite.Statement) => _statementKeywords.Concat(_expressionKeywords)
				.Distinct(),
			(CompletionPlace.Name, _) => _expressionKeywords,
			(CompletionPlace.Type, _) => _typeKeywords,
			(CompletionPlace.Member or CompletionPlace.Trait or CompletionPlace.Label, _) => [],
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
			
			foreach (var group in probe.Importable.Where(static candidate => IsListed(candidate.Import!.Name))
				         .GroupBy(static candidate => (Import: candidate.Import!, IsFunction(candidate))))
			{
				if (ToImportEdit(probe, group.Key.Import, text, inserted) is not { } edit)
					continue;
				
				var item = group.Key.Item2
					? ToFunctionItem(probe, group.Key.Import.Name, [..group])
					: ToItem(probe, group.First());
				
				item["labelDetails"]!["description"] = group.Key.Import.Module;
				item["additionalTextEdits"] = new JsonArray(edit);
				items.Add(item);
			}
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
		ParameterMode.Imm => "imm ",
		_ => ""
	};
	
	private static int RankOf(CompletionProbe probe, CompletionCandidate candidate)
	{
		var fits = Fits(probe, candidate);
		return candidate.Import switch
		{
			{ InProject: true } => fits ? 40 : 41,
			not null => fits ? 50 : 51,
			_ when fits => 0,
			_ => candidate.Symbol is LocalVariableSymbol or ParameterSymbol or FieldSymbol or PropertySymbol ? 1 : 2
		};
	}
	
	private static bool Fits(CompletionProbe probe, CompletionCandidate candidate)
	{
		if (probe.Place == CompletionPlace.Member && candidate.Symbol is ModulePathSymbol)
			return true;
		
		if (probe.ExpectedType is not { } expected)
			return false;
		
		if (expected is FunctionType)
			return IsFunction(candidate);
		
		var type = candidate switch
		{
			{ Function: { } function } => function.Signature.ReturnType,
			{ Type: { } declared } => declared,
			{ Global: { } global } => global.Type,
			{ Symbol: LocalVariableSymbol local } => local.Type,
			{ Symbol: TypeSymbol named } => named,
			_ => null
		};
		
		return type is not null && (type == expected || type.OriginalDefinition == expected.OriginalDefinition);
	}
	
	private static JsonObject? ToImportEdit(CompletionProbe probe, CompletionImport import, string text, int inserted)
	{
		if (probe.File is not { } file)
			return null;
		
		var edit = ImportEdits.Create(file, import.Module, import.Name);
		return new JsonObject
		{
			["range"] = new JsonObject
			{
				["start"] = ToPosition(text, ToOriginal(edit.Location.Range.Start)),
				["end"] = ToPosition(text, ToOriginal(edit.Location.Range.End))
			},
			["newText"] = edit.NewText
		};
		
		int ToOriginal(int offset) => offset >= probe.Offset + inserted ? offset - inserted : offset;
	}
	
	private static JsonObject ToPosition(string text, int offset)
	{
		var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
		return new JsonObject { ["line"] = text.AsSpan(0, lineStart).Count('\n'), ["character"] = offset - lineStart };
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
		TypeSymbol { OriginalDefinition: RecordSymbol { IsOwn: true } } => ItemKind.Class,
		TypeSymbol { OriginalDefinition: RecordSymbol } => ItemKind.Struct,
		TypeSymbol { OriginalDefinition: EnumSymbol } => ItemKind.Enum,
		ModulePathSymbol => ItemKind.Module,
		LabelSymbol => ItemKind.Reference,
		MethodSymbol => ItemKind.Method,
		FunctionSymbol => ItemKind.Function,
		_ => ItemKind.Keyword
	};
}