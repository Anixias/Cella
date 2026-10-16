using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed class TypeParser(ImmutableArray<Token> tokens) : BaseParser<ITypeNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _externalKeywords =
		new[] { TokenType.KeywordExt }.ToDictionary(static t => t.Representation);
	
	public override ITypeNode Parse(ref int index) => ParseType(ref index);
	
	private ITypeNode ParseType(ref int index)
	{
		if (Match(ref index, out var borrowKeyword, TokenSets.BorrowKeywords))
			return ParseBorrowType(ref index, borrowKeyword);
		
		if (Match(ref index, out var dynKeyword, TokenType.KeywordDyn))
			return ParseDynType(ref index, dynKeyword);
		
		if (Peek(index, TokenType.KeywordOwn) && Peek(index + 1, TokenType.KeywordFun))
			throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, Tokens[index].SourceLocation,
				"Cannot name 'own fun' types"));
		
		if (TryParseFunctionType(ref index) is { } functionType)
			return functionType;
		
		if (!Match(ref index, out var identifier, TokenType.Identifier) &&
		    !(Peek(index + 1, TokenType.OpDot) && Match(ref index, out identifier, TokenType.KeywordMod)))
			throw Expected(index, "a type");
		
		var qualified = Peek(index, TokenType.OpDot) ? ParseQualifiedType(ref index, identifier) : null;
		var name = qualified?.Parts[^1] ?? identifier;
		var startIndex = index;
		if (!Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			return qualified ?? (ITypeNode)new IdentifierTypeNode(identifier);
		
		// Disallow the open bracket being on another line from the identifier; backtrack to unconsume open bracket
		if (openBracket.Line != name.Line)
		{
			index = startIndex;
			return qualified ?? (ITypeNode)new IdentifierTypeNode(identifier);
		}
		
		var args = new List<IGenericArgumentNode> { ParseGenericArgument(ref index) };
		while (MatchSeparator(ref index, TokenType.OpCloseBracket))
			args.Add(ParseGenericArgument(ref index));
		
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw ExpectedClose(index, openBracket);
		
		var (source, range) = identifier.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		
		return new GenericTypeNode(new(source, range), name, args)
		{
			Qualifiers = qualified is null ? [] : qualified.Parts[..^1]
		};
	}
	
	private BorrowTypeNode ParseBorrowType(ref int index, Token keyword)
	{
		var openBracket = MatchOpenBracket(ref index);
		var target = ParseType(ref index);
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw ExpectedClose(index, openBracket);
		
		return new BorrowTypeNode(keyword, target, closeBracket);
	}
	
	private DynTypeNode ParseDynType(ref int index, Token keyword)
	{
		var openBracket = MatchOpenBracket(ref index);
		if (!Peek(index, TokenType.Identifier) && !Peek(index, TokenType.KeywordFun) &&
		    !Peek(index, TokenType.KeywordRef))
			throw Expected(index, "a trait");
		
		var trait = ParseType(ref index);
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw ExpectedClose(index, openBracket);
		
		return new DynTypeNode(keyword, trait, closeBracket);
	}
	
	private Token MatchOpenBracket(ref int index)
	{
		if (!AtEnd(index) && Tokens[index].Line != Tokens[index - 1].Line)
			throw Expected(index - 1, "'['");
		
		return Match(ref index, out var openBracket, TokenType.OpOpenBracket)
			? openBracket
			: throw Expected(index, "'['");
	}
	
	private ParseException ExpectedClose(int index, Token openBracket)
	{
		var at = Math.Min(index, Tokens.Length - 1);
		if (at > 0 && Tokens[at].Error is null && Tokens[at].Line != Tokens[at - 1].Line)
			at--;
		
		var token = Tokens[at];
		return new(new Diagnostic(DiagnosticSeverity.Error, token.SourceLocation, token.Error ?? "Expected ']'")
		{
			Hints = token.Error is null ? [$"To match the '[' on line {openBracket.Line}"] : []
		});
	}
	
	private ParseException Expected(int index, string expected)
	{
		var token = Tokens[Math.Min(index, Tokens.Length - 1)];
		return new(
			new Diagnostic(DiagnosticSeverity.Error, token.SourceLocation, token.Error ?? $"Expected {expected}"));
	}
	
	private QualifiedTypeNode ParseQualifiedType(ref int index, Token first)
	{
		var parts = new List<Token> { first };
		while (Match(ref index, TokenType.OpDot))
		{
			if (!Match(ref index, out var part, TokenType.Identifier))
				throw Expected(index, "a name");
			
			parts.Add(part);
		}
		
		var (source, range) = first.SourceLocation;
		range = range.Join(parts[^1].SourceLocation.Range);
		return new QualifiedTypeNode(new(source, range), parts);
	}
	
	private FunctionTypeNode? TryParseFunctionType(ref int index)
	{
		var start = index;
		var first = Tokens[index];
		var isRef = Match(ref index, TokenType.KeywordRef);
		if (isRef && !Peek(index, TokenType.KeywordFun))
			throw Expected(index, "'fun'");
		
		var isExternal = !isRef && Match(ref index, _externalKeywords, TokenType.KeywordExt);
		if (!Match(ref index, out var keyword, TokenType.KeywordFun))
		{
			index = start;
			return null;
		}
		
		MatchOpenBracket(ref index);
		var parameterModes = new List<Token?>();
		var parameterTypes = new List<ITypeNode>();
		var ellipses = new List<Token?>();
		Token? variadic = null;
		if (!Peek(index, TokenType.OpArrow) && !Peek(index, TokenType.OpCloseBracket))
		{
			do
			{
				if (isExternal && Peek(index, TokenType.OpDotDotDot) &&
				    (Peek(index + 1, TokenType.OpArrow) || Peek(index + 1, TokenType.OpCloseBracket)))
				{
					if (parameterTypes.Count == 0)
						throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, Tokens[index].SourceLocation,
							"Expected a parameter before '...'"));
					
					variadic = Tokens[index++];
					break;
				}
				
				var isBorrowType = (Peek(index, TokenType.KeywordMut) || Peek(index, TokenType.KeywordImm)) &&
				                   Peek(index + 1, TokenType.OpOpenBracket);
				
				parameterModes.Add(!isBorrowType && Match(ref index, out var mode, TokenSets.ParameterModes)
					? mode
					: null);
				
				ellipses.Add(Match(ref index, out var ellipsis, TokenType.OpDotDotDot) ? ellipsis : null);
				parameterTypes.Add(ParseType(ref index));
			} while (MatchSeparator(ref index, TokenType.OpCloseBracket, TokenType.OpArrow));
		}
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw Expected(index, "']'");
		
		var (source, range) = first.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		return new FunctionTypeNode(new(source, range), keyword, isExternal, isRef, parameterModes, parameterTypes,
			returnType)
		{
			Ellipses = [..ellipses],
			Variadic = variadic
		};
	}
	
	private IGenericArgumentNode ParseGenericArgument(ref int index)
	{
		if (Peek(index, TokenType.KeywordImm) || Peek(index, TokenType.KeywordMut) || Peek(index, TokenType.KeywordDyn))
			return new TypeArgumentNode(ParseType(ref index));
		
		if (TryParseFunctionType(ref index) is { } functionType)
			return new TypeArgumentNode(functionType);
		
		if (TryParseExpression(ref index) is not { } expression)
			return new TypeArgumentNode(ParseType(ref index));
		
		if (expression is VarExpressionNode var)
			return new IdentifierArgumentNode(var.Identifier);
		
		return new ExpressionArgumentNode(expression);
	}
	
	private IExpressionNode ParseExpression(ref int index) => new ExpressionParser(Tokens).Parse(ref index);
	
	private IExpressionNode? TryParseExpression(ref int index)
	{
		try
		{
			var parserIndex = index;
			var result = ParseExpression(ref parserIndex);
			index = parserIndex;
			return result;
		}
		catch (ParseException e) when (e.Diagnostic.SourceLocation.Range.Start >
		                               Tokens[index].SourceLocation.Range.Start)
		{
			throw;
		}
		catch (ParseException)
		{
			return null;
		}
	}
}