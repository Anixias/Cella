using System.Text.Json;
using System.Text.Json.Nodes;
using Cella.Compiler.Projects;
using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.LanguageServer;

public static class SemanticTokens
{
	private enum Kind
	{
		Type,
		Struct,
		Enum,
		Interface,
		TypeParameter,
		Function,
		Method,
		Parameter,
		Variable,
		Property,
		EnumMember,
		Keyword,
		Namespace
	}
	
	[Flags]
	private enum Modifiers
	{
		None = 0,
		Declaration = 1,
		Readonly = 2,
		Static = 4,
		Drop = 8,
		Ref = 16,
		DefaultLibrary = 32,
		Accessor = 64
	}
	
	private readonly record struct Classified(SourceLocation Location, Kind Kind, Modifiers Modifiers, bool IsKeyword);
	
	public static JsonObject Legend { get; } = new()
	{
		["tokenTypes"] = Names(Enum.GetNames<Kind>()),
		["tokenModifiers"] = Names(Enum.GetNames<Modifiers>().Where(static name => name != nameof(Modifiers.None)))
	};
	
	public static JsonObject Encode(IEnumerable<SymbolReference> references, IEnumerable<Token> keywords) =>
		Encode(references.Select(Describe).OfType<Classified>(), keywords.Select(static keyword =>
			new Classified(keyword.SourceLocation, Kind.Keyword, Modifiers.None, true)));
	
	private static JsonObject Encode(IEnumerable<Classified> names, IEnumerable<Classified> keywords)
	{
		var data = new JsonArray();
		var (previousLine, previousCharacter, previousEnd) = (0, 0, 0);
		var classified = names
			.Concat(keywords)
			.OrderBy(static token => token.Location.Range.Start)
			.ThenBy(static token => token.Location.Range.Length)
			.ThenBy(static token => !token.IsKeyword);
		
		foreach (var ((source, range), kind, modifiers, _) in classified)
		{
			if (range.Start < previousEnd)
				continue;
			
			var (line, character) = Protocol.ToLineCharacter(source, range.Start);
			data.Add(line - previousLine);
			data.Add(line == previousLine ? character - previousCharacter : character);
			data.Add(range.Length);
			data.Add((int)kind);
			data.Add((int)modifiers);
			(previousLine, previousCharacter, previousEnd) = (line, character, range.End);
		}
		
		return new JsonObject { ["data"] = data };
	}
	
	public static JsonObject EncodeProject(string text, IEnumerable<TextRange> settings,
		IEnumerable<SymbolReference> references)
	{
		var source = new StringSource(text, string.Empty);
		return Encode(references.Select(Describe).OfType<Classified>(), settings.Select(range =>
			new Classified(new SourceLocation(source, range), Kind.Property, Modifiers.None, false)));
	}
	
	public static JsonObject EncodeDescription(SymbolReference reference, SemanticModel model)
	{
		var text = reference.Description;
		var tokens = new FilteredScanner(new StringSource(text, string.Empty)).ToList();
		var names = new HashSet<string> { reference.Location.GetText().ToString(), NameOf(reference.Symbol) };
		var isModule = text.StartsWith("mod ", StringComparison.Ordinal);
		var data = new JsonArray();
		var groups = 0;
		for (var i = 0; i < tokens.Count; i++)
		{
			var token = tokens[i];
			groups += token.Type switch
			{
				TokenType.OpOpenParen or TokenType.OpOpenBracket => 1,
				TokenType.OpCloseParen or TokenType.OpCloseBracket => -1,
				_ => 0
			};
			
			if (token.Type != TokenType.Identifier)
				continue;
			
			var namesParameter = groups > 0 && i + 1 < tokens.Count && tokens[i + 1].Type == TokenType.OpColon;
			(Kind, Modifiers)? classified = isModule ? (Kind.Namespace, Modifiers.None)
				: namesParameter && reference.Symbol is EnumCaseSymbol ? (Kind.Property, Modifiers.Readonly)
				: namesParameter ? (Kind.Parameter, Modifiers.None)
				: names.Contains(token.Text) && groups == 0 ? Classify(reference)
				: model.FindType(token.Text) is { } type ? Classify(type)
				: null;
			
			if (classified is not var (kind, modifiers))
				continue;
			
			var range = token.SourceLocation.Range;
			data.Add(range.Start);
			data.Add(range.Length);
			data.Add((int)kind);
			data.Add((int)modifiers);
		}
		
		return new JsonObject { ["text"] = text, ["tokens"] = data };
	}
	
	private static string NameOf(Symbol symbol) => symbol switch
	{
		FunctionSymbol function => function.DeclaredName,
		MethodSymbol method => method.Function.DeclaredName,
		_ => symbol.Name
	};
	
	private static Classified? Describe(SymbolReference reference)
	{
		var (source, range) = reference.Location;
		var text = reference.Location.GetText().ToString();
		var isOperator = TokenTypeInfo.GetOperator(text) is not null &&
			reference.Symbol is FunctionSymbol or MethodSymbol || IsKeywordOperator(reference.Symbol);
		
		if (!isOperator && !IsName(text) && !NamesProjectEntry(reference, text) ||
		    Classify(reference) is not var (kind, modifiers))
			return null;
		
		if (isOperator)
			kind = Kind.Method;
		
		if (reference.Declaration is { } declaration && declaration.Range.Start == range.Start &&
		    Protocol.PathComparer.Equals(declaration.Source.FilePath, source.FilePath))
			modifiers |= Modifiers.Declaration;
		
		return new Classified(reference.Location, kind, modifiers, false);
	}
	
	private static bool IsKeywordOperator(Symbol symbol) => symbol is FunctionSymbol
	{
		Syntax: FunctionNode { Identifier.Type: TokenType.KeywordPanic or TokenType.KeywordIn or TokenType.KeywordFor }
	};
	
	private static JsonArray Names(IEnumerable<string> names) =>
		new([..names.Select(static name => JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(name)))]);
	
	private static bool NamesProjectEntry(SymbolReference reference, string text) =>
		CellaProject.IsProjectFile(reference.Location.Source.FilePath) && reference.Symbol switch
		{
			FunctionSymbol function => function.DeclaredName,
			var symbol => symbol.Name
		} == text;
	
	private static bool IsName(string text) =>
		new Scanner(new StringSource(text, string.Empty)).ScanToken(0) is
			{ Token.Type: TokenType.Identifier, NextPosition: var end } && end == text.Length;
	
	private static (Kind, Modifiers)? Classify(SymbolReference reference) => reference.Symbol switch
	{
		FunctionSymbol { Kind: FunctionKind.Free or FunctionKind.External } => (Kind.Function, Modifiers.None),
		FunctionSymbol or MethodSymbol => (Kind.Method, Modifiers.None),
		LocalVariableSymbol { IsConstant: true } => (Kind.Variable, Modifiers.Static | Modifiers.Readonly),
		LocalVariableSymbol { IsMutable: true } => (Kind.Variable, Modifiers.None),
		LocalVariableSymbol => (Kind.Variable, Modifiers.Readonly),
		GlobalSymbol { IsMutable: true } => (Kind.Variable, Modifiers.Static),
		GlobalSymbol => (Kind.Variable, Modifiers.Static | Modifiers.Readonly),
		ParameterSymbol => (Kind.Parameter, Modifiers.None),
		PropertySymbol => (Kind.Property, Modifiers.Accessor),
		FieldSymbol { IsMutable: true } => (Kind.Property, Modifiers.None),
		FieldSymbol => (Kind.Property, Modifiers.Readonly),
		EnumCaseSymbol => (Kind.EnumMember, Modifiers.None),
		TraitSymbol or TraitType => (Kind.Interface, Modifiers.None),
		TypeParameterSymbol => (Kind.TypeParameter, Modifiers.None),
		TypeSymbol { OriginalDefinition: RecordSymbol record } => (Kind.Struct,
			(reference.NeedsDrop ? Modifiers.Drop : Modifiers.None) | (record.IsRef ? Modifiers.Ref : Modifiers.None)),
		TypeSymbol { OriginalDefinition: EnumSymbol } => (Kind.Enum, Modifiers.None),
		NamedTypeSymbol => (Kind.Type, Modifiers.None),
		TypeSymbol => (Kind.Keyword, Modifiers.DefaultLibrary),
		ModulePathSymbol => (Kind.Namespace, Modifiers.None),
		_ => null
	};
}