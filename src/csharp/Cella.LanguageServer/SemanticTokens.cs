using System.Text.Json;
using System.Text.Json.Nodes;
using Cella.Core.Binding;
using Cella.Core.Symbols;
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
		Keyword
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
		DefaultLibrary = 32
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
	
	public static JsonObject EncodeSettings(string text, IEnumerable<TextRange> settings)
	{
		var source = new StringSource(text, string.Empty);
		return Encode([], settings.Select(range =>
			new Classified(new SourceLocation(source, range), Kind.Property, Modifiers.None, false)));
	}
	
	private static Classified? Describe(SymbolReference reference)
	{
		var (source, range) = reference.Location;
		var text = reference.Location.GetText().ToString();
		var isOperator = TokenTypeInfo.GetOperator(text) is not null &&
		                 reference.Symbol is FunctionSymbol or MethodSymbol;
		
		if (!isOperator && !IsName(text) || Classify(reference) is not var (kind, modifiers))
			return null;
		
		if (isOperator)
			kind = Kind.Method;
		
		if (reference.Declaration is { } declaration && declaration.Range.Start == range.Start &&
		    Protocol.PathComparer.Equals(declaration.Source.FilePath, source.FilePath))
			modifiers |= Modifiers.Declaration;
		
		return new Classified(reference.Location, kind, modifiers, false);
	}
	
	private static JsonArray Names(IEnumerable<string> names) =>
		new([..names.Select(static name => JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(name)))]);
	
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
		FieldSymbol { IsMutable: true } or PropertySymbol => (Kind.Property, Modifiers.None),
		FieldSymbol => (Kind.Property, Modifiers.Readonly),
		EnumCaseSymbol => (Kind.EnumMember, Modifiers.None),
		TraitSymbol or TraitType => (Kind.Interface, Modifiers.None),
		TypeParameterSymbol => (Kind.TypeParameter, Modifiers.None),
		TypeSymbol { OriginalDefinition: RecordSymbol record } => (Kind.Struct,
			(reference.NeedsDrop ? Modifiers.Drop : Modifiers.None) | (record.IsRef ? Modifiers.Ref : Modifiers.None)),
		TypeSymbol { OriginalDefinition: EnumSymbol } => (Kind.Enum, Modifiers.None),
		NamedTypeSymbol => (Kind.Type, Modifiers.None),
		TypeSymbol => (Kind.Keyword, Modifiers.DefaultLibrary),
		_ => null
	};
}