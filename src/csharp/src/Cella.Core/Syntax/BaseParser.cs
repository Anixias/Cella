using System.Collections.Immutable;
using Cella.Core.Text;

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
	
	protected bool AtEnd(int index) => index < 0 || index >= Tokens.Length || Tokens[index].Type == TokenType.EndOfFile;
	
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
		token = Reinterpret(Tokens[index], contextualTypes);
		
		if (!types.Contains(token.Type))
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
		token = Reinterpret(Tokens[index], contextualTypes);
		
		if (token.Type != type)
			return false;
		
		index++;
		return true;
	}
	
	protected void SkipUntil(ref int index, IReadOnlySet<TokenType> types) =>
		SkipUntil(ref index, null, types);
	
	protected void SkipUntil(ref int index, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> types)
	{
		while (index < Tokens.Length)
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
		while (index < Tokens.Length)
		{
			var token = Reinterpret(Tokens[index], contextualTypes);
			
			// Do not consume the matched token
			if (token.Type == type)
				return;
			
			index++;
		}
	}
	
	protected bool Consume(ref int index, IReadOnlySet<TokenType> syncTypes, IReadOnlySet<TokenType> types) =>
		Consume(ref index, out _, null, syncTypes, types);
	
	protected bool Consume(ref int index, out Token token, IReadOnlySet<TokenType> syncTypes,
		IReadOnlySet<TokenType> types) =>
		Consume(ref index, out token, null, syncTypes, types);
	
	protected bool Consume(ref int index, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> syncTypes, IReadOnlySet<TokenType> types) =>
		Consume(ref index, out _, contextualTypes, syncTypes, types);
	
	protected bool Consume(ref int index, out Token token, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> syncTypes, IReadOnlySet<TokenType> types)
	{
		token = Reinterpret(Tokens[index], contextualTypes);
		
		if (!types.Contains(token.Type))
		{
			SkipUntil(ref index, contextualTypes, syncTypes);
			return false;
		}
		
		index++;
		return true;
	}
	
	protected bool Consume(ref int index, IReadOnlySet<TokenType> syncTypes, TokenType type) =>
		Consume(ref index, out _, null, syncTypes, type);
	
	protected bool Consume(ref int index, out Token token, IReadOnlySet<TokenType> syncTypes, TokenType type) =>
		Consume(ref index, out token, null, syncTypes, type);
	
	protected bool Consume(ref int index, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> syncTypes, TokenType type) =>
		Consume(ref index, out _, contextualTypes, syncTypes, type);
	
	protected bool Consume(ref int index, out Token token, IReadOnlyDictionary<string, TokenType>? contextualTypes,
		IReadOnlySet<TokenType> syncTypes, TokenType type)
	{
		token = Reinterpret(Tokens[index], contextualTypes);
		
		if (token.Type != type)
		{
			SkipUntil(ref index, contextualTypes, syncTypes);
			return false;
		}
		
		index++;
		return true;
	}
	
	private static Token Reinterpret(Token token, IReadOnlyDictionary<string, TokenType>? contextualTypes)
	{
		if (contextualTypes is null || token.Type != TokenType.Identifier)
			return token;
		
		var tokenText = new string(token.GetText());
		if (contextualTypes.TryGetValue(tokenText, out var context))
			return token with { Type = context };
		
		return token;
	}
}