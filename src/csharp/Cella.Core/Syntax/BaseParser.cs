using System.Collections.Immutable;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public abstract class BaseParser<T>(ImmutableArray<Token> tokens) : IParser<T>
{
	protected readonly ImmutableArray<Token> Tokens = tokens;
	
	public abstract T? Parse(ref int index);
	
	public T? Parse()
	{
		var index = 0;
		return Parse(ref index);
	}
	
	protected bool IsReservedWord(int index) =>
		index >= 0 && index < Tokens.Length && Tokens[index] is { Type: not TokenType.Identifier } token &&
		TokenTypeInfo.GetKeyword(token.Text) == token.Type && !token.Type.IsContextual;
	
	protected static string DescribeReservedWord(Token token) => $"'{token.Text}' is a keyword";
	
	protected bool MatchSeparator(ref int index, params ReadOnlySpan<TokenType> closers) =>
		Match(ref index, out var comma, TokenType.OpComma) && !RejectTrailingComma(index, comma, closers);
	
	protected bool RejectTrailingComma(int index, Token comma, params ReadOnlySpan<TokenType> closers)
	{
		foreach (var closer in closers)
		{
			if (!Peek(index, closer))
				continue;
			
			ReportTrailingComma(new(DiagnosticSeverity.Error, comma.SourceLocation, "Cannot end lists with ','"));
			return true;
		}
		
		return false;
	}
	
	protected virtual void ReportTrailingComma(Diagnostic diagnostic) => throw new ParseException(diagnostic);
	
	protected bool AtEnd(int index) => index < 0 || index >= Tokens.Length || Tokens[index].Type == TokenType.EndOfFile;
	
	protected bool Peek(int index, TokenType type) =>
		!AtEnd(index) && Tokens[index].Type == type;
	
	protected bool Match(ref int index, IReadOnlySet<TokenType> types) =>
		Match(ref index, out _, null, types);
	
	protected bool Match(ref int index, out Token token, IReadOnlySet<TokenType> types) =>
		Match(ref index, out token, null, types);
	
	protected bool Match(ref int index, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> types) =>
		Match(ref index, out _, contextualTypes, types);
	
	protected bool Match(ref int index, out Token token, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> types)
	{
		if (!TryGetToken(index, contextualTypes, out token) || !types.Contains(token.Type))
			return false;
		
		index++;
		return true;
	}
	
	protected bool Match(ref int index, TokenType type) =>
		Match(ref index, out _, null, type);
	
	protected bool Match(ref int index, out Token token, TokenType type) =>
		Match(ref index, out token, null, type);
	
	protected bool Match(ref int index, IReadOnlyDictionary<string, TokenType>? contextualTypes, TokenType type) =>
		Match(ref index, out _, contextualTypes, type);
	
	protected bool Match(ref int index, out Token token, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		TokenType type)
	{
		if (!TryGetToken(index, contextualTypes, out token) || token.Type != type)
			return false;
		
		index++;
		return true;
	}
	
	protected void SkipUntil(ref int index, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> types)
	{
		while (!AtEnd(index))
		{
			var token = Reinterpret(Tokens[index], contextualTypes);
			
			// Do not consume the matched token
			if (types.Contains(token.Type))
				return;
			
			index++;
		}
	}
	
	protected void SkipUntil(ref int index, TokenType type) =>
		SkipUntil(ref index, null, type);
	
	protected void SkipUntil(ref int index, IReadOnlyDictionary<string, TokenType>? contextualTypes, TokenType type)
	{
		while (!AtEnd(index))
		{
			var token = Reinterpret(Tokens[index], contextualTypes);
			
			// Do not consume the matched token
			if (token.Type == type)
				return;
			
			index++;
		}
	}
	
	protected bool Consume(ref int index, out Token token, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> syncTypes, TokenType type)
	{
		if (!TryGetToken(index, contextualTypes, out token))
			return false;
		
		if (token.Type != type)
		{
			SkipUntil(ref index, contextualTypes, syncTypes);
			return false;
		}
		
		index++;
		return true;
	}
	
	private bool TryGetToken(int index, IReadOnlyDictionary<string, TokenType>? contextualTypes, out Token token)
	{
		if (index < 0 || index >= Tokens.Length)
		{
			token = default;
			return false;
		}
		
		token = Reinterpret(Tokens[index], contextualTypes);
		return true;
	}
	
	private static Token Reinterpret(Token token, IReadOnlyDictionary<string, TokenType>? contextualTypes)
	{
		if (contextualTypes is null || token.Type != TokenType.Identifier)
			return token;
		
		var tokenText = new string(token.Text);
		if (contextualTypes.TryGetValue(tokenText, out var context))
			return token with { Type = context };
		
		return token;
	}
}