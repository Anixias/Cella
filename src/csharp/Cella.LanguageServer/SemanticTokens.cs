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
		Ref = 16
	}
	
	public static JsonObject Legend { get; } = new()
	{
		["tokenTypes"] = Names(Enum.GetNames<Kind>()),
		["tokenModifiers"] = Names(Enum.GetNames<Modifiers>().Where(static name => name != nameof(Modifiers.None)))
	};
	
	public static JsonObject Encode(IEnumerable<SymbolReference> references)
	{
		var data = new JsonArray();
		var (previousLine, previousCharacter, previousEnd) = (0, 0, 0);
		foreach (var reference in references.OrderBy(static reference => reference.Location.Range.Start)
			         .ThenBy(static reference => reference.Location.Range.Length))
		{
			var (source, range) = reference.Location;
			var text = reference.Location.GetText().ToString();
			var isOperator = TokenTypeInfo.GetOperator(text) is not null &&
			                 reference.Symbol is FunctionSymbol or MethodSymbol;
			
			if (range.Start < previousEnd || !isOperator && !IsName(text) ||
			    Classify(reference) is not { } classified)
				continue;
			
			var (kind, modifiers) = classified;
			if (isOperator)
				kind = Kind.Method;
			
			if (reference.Declaration is { } declaration && declaration.Range.Start == range.Start &&
			    Protocol.PathComparer.Equals(declaration.Source.FilePath, source.FilePath))
				modifiers |= Modifiers.Declaration;
			
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
		TypeSymbol => (Kind.Keyword, Modifiers.None),
		_ => null
	};
}