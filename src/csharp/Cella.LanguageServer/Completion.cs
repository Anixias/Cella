using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.LanguageServer;

public static partial class Completion
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
	
	private static readonly string[] _typeKeywords = ["dyn", "fun", "imm", "mut", "array", "ptr", "fstr"];
	
	private static readonly string[] _declarationKeywords =
	[
		"fun", "rec", "enum", "union", "trait", "impl", "ref", "const", "val", "var", "ext", "pub", "pvt", "mod", "op",
		"prop", "get", "set", "req"
	];
	
	private static readonly string[] _topLevelKeywords = ["mod", "use", "ext", "pub", "pvt"];
	
	[GeneratedRegex(@"^\s*\S+(\[[^\]]*\])?\s*:\s*((pub|pvt|mod|ref|ext)\s+)*$")]
	private static partial Regex DeclarationStart();
	
	[GeneratedRegex(@"\bmatch\b")]
	private static partial Regex MatchKeyword();
	
	[GeneratedRegex(@"^\s*use\b")]
	private static partial Regex UseKeyword();
	
	public static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
	
	public static int FindNameStart(string text, int offset)
	{
		var start = offset;
		while (start > 0 && IsNameCharacter(text[start - 1]))
			start--;
		
		return start;
	}
	
	public static string Repair(string text, int offset)
	{
		var lineStart = LineStartOf(text, offset);
		var lineEnd = text.IndexOf('\n', offset) is >= 0 and var end ? end : text.Length;
		if (lineEnd > offset && text[lineEnd - 1] == '\r')
			lineEnd--;
		
		var line = text[lineStart..lineEnd];
		if (UseKeyword().IsMatch(line))
			return line.Count(static c => c == '[') > line.Count(static c => c == ']')
				? text.Insert(lineEnd, "]")
				: text;
		
		return CompleteArm(text, lineStart, lineEnd);
	}
	
	private static string CompleteArm(string text, int lineStart, int lineEnd)
	{
		if (text[lineStart..lineEnd].Contains("=>") || FindOpenBrace(text, lineStart) is not { } brace)
			return text;
		
		var braceLineStart = LineStartOf(text, brace);
		var header = text[braceLineStart..brace];
		if (string.IsNullOrWhiteSpace(header) && braceLineStart > 0)
			header = text[LineStartOf(text, braceLineStart - 1)..(braceLineStart - 1)];
		
		return MatchKeyword().IsMatch(header) ? text.Insert(lineEnd, " => 0") : text;
	}
	
	private static int LineStartOf(string text, int offset) => text.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1;
	
	private static int? FindOpenBrace(string text, int offset)
	{
		var depth = 0;
		for (var i = offset - 1; i >= 0; i--)
		{
			if (text[i] == '}')
				depth++;
			else if (text[i] == '{' && depth-- == 0)
				return i;
		}
		
		return null;
	}
	
	public static bool IsEmptyArgument(string text, int offset)
	{
		var before = text[..offset].TrimEnd();
		var after = text[offset..].TrimStart();
		return before is [.., '(' or ','] && after is [')' or ',', ..];
	}
	
	public static (int NameEnd, string Callee, int Argument)? FindCall(string text, int offset)
	{
		var tokens = new FilteredScanner(new StringSource(text, string.Empty))
			.TakeWhile(token => token.Type != TokenType.EndOfFile && token.SourceLocation.Range.Start < offset)
			.ToList();
		
		var depth = 0;
		var argument = 0;
		var open = -1;
		for (var i = tokens.Count - 1; i >= 0 && open < 0; i--)
		{
			switch (tokens[i].Type)
			{
				case TokenType.OpCloseParen or TokenType.OpCloseBracket or TokenType.OpCloseBrace:
					depth++;
					break;
				
				case TokenType.OpOpenParen when depth == 0:
					open = tokens[i].SourceLocation.Range.Start;
					break;
				
				case TokenType.OpOpenBracket or TokenType.OpOpenBrace when depth == 0:
					return null;
				
				case TokenType.OpOpenParen or TokenType.OpOpenBracket or TokenType.OpOpenBrace:
					depth--;
					break;
				
				case TokenType.OpComma when depth == 0:
					argument++;
					break;
			}
		}
		
		var end = open;
		while (end > 0 && char.IsWhiteSpace(text[end - 1]))
			end--;
		
		if (end > 0 && text[end - 1] == ']')
		{
			var brackets = 0;
			while (end > 0)
			{
				var c = text[--end];
				brackets += c == ']' ? 1 : c == '[' ? -1 : 0;
				if (brackets == 0)
					break;
			}
		}
		
		var start = end > 0 ? FindNameStart(text, end) : end;
		return start < end ? (end, text[start..end], argument) : null;
	}
	
	public static JsonObject ToList(CompletionProbe? probe, string text, int nameStart)
	{
		var lineStart = text.LastIndexOf('\n', Math.Max(nameStart - 1, 0)) + 1;
		var before = nameStart > lineStart ? text[lineStart..nameStart] : "";
		var items = new JsonArray();
		IEnumerable<string> keywords = probe?.Place switch
		{
			CompletionPlace.Name when string.IsNullOrWhiteSpace(before) =>
				_statementKeywords.Concat(_expressionKeywords).Distinct(),
			CompletionPlace.Name => _expressionKeywords,
			CompletionPlace.Type => _typeKeywords,
			CompletionPlace.Member => [],
			_ when DeclarationStart().IsMatch(before) => _declarationKeywords,
			_ when string.IsNullOrWhiteSpace(before) => _topLevelKeywords,
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
	
	public static JsonObject? ToSignatureHelp(CompletionProbe probe, string callee, int activeParameter)
	{
		var functions = probe.Candidates
			.Where(candidate => IsFunction(candidate) && NameOf(candidate) == callee)
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
			["sortText"] = $"{RankOf(probe, candidate)}{name}",
			["insertText"] = name,
			["insertTextFormat"] = PlainText
		};
	}
	
	private static JsonObject ToFunctionItem(CompletionProbe probe, string name, List<CompletionCandidate> overloads)
	{
		var functions = overloads.Select(static overload => overload.Function!.Value).ToList();
		var detail = SemanticModel.Describe(overloads[0]);
		if (overloads.Count > 1)
			detail += overloads.Count == 2 ? " (+1 overload)" : $" (+{overloads.Count - 1} overloads)";
		
		var item = new JsonObject
		{
			["label"] = name,
			["kind"] = (int)(overloads.Any(static overload => overload.Symbol is MethodSymbol)
				? ItemKind.Method
				: ItemKind.Function),
			["detail"] = detail,
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