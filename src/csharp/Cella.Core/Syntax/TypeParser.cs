using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed class TypeParser(ImmutableArray<Token> tokens, List<Token>? contextualKeywords = null)
	: BaseParser<ITypeNode>(tokens, contextualKeywords)
{
	private static readonly Dictionary<string, TokenType> _externalKeywords =
		new[] { TokenType.KeywordExt }.ToDictionary(static t => t.Representation);
	
	public delegate TypeParameterNode DeclaredParameterParser(ref int index);
	
	public Action<Diagnostic>? ReportRecovered { get; init; }
	public DeclaredParameterParser? ParseDeclaredParameter { get; init; }
	public List<TypeParameterNode>? Declared { get; init; }
	
	public override ITypeNode Parse(ref int index) => ParseType(ref index);
	
	private ITypeNode ParseType(ref int index)
	{
		EnsureStack(index);
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
		
		if (Match(ref index, out var emptyClose, TokenType.OpCloseBracket))
		{
			var (nameSource, nameRange) = identifier.SourceLocation;
			var (bracketSource, bracketRange) = openBracket.SourceLocation;
			return new GenericTypeNode(new(nameSource, nameRange.Join(emptyClose.SourceLocation.Range)), name, [])
			{
				Qualifiers = qualified is null ? [] : qualified.Parts[..^1],
				EmptyBrackets = new(bracketSource, bracketRange.Join(emptyClose.SourceLocation.Range))
			};
		}
		
		var args = new List<IGenericArgumentNode>();
		Token closeBracket;
		while (true)
		{
			if (EndsTypeLine(index))
			{
				ReportRecovered!(ExpectedClose(index, openBracket).Diagnostic);
				if (args.Count == 0)
					return qualified ?? (ITypeNode)new IdentifierTypeNode(identifier);
				
				closeBracket = Tokens[index - 1];
				break;
			}
			
			if (StartsMemberDeclaration(index))
				throw ExpectedClose(index, openBracket);
			
			args.Add(ParseGenericArgument(ref index));
			if (MatchSeparator(ref index, TokenType.OpCloseBracket))
				continue;
			
			if (Match(ref index, out closeBracket, TokenType.OpCloseBracket))
				break;
			
			if (!EndsTypeLine(index))
				throw ExpectedClose(index, openBracket);
		}
		
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
	
	private bool EndsTypeLine(int index) =>
		ReportRecovered is not null && StartsLine(index) &&
		(AtEnd(index) || Tokens[index].Type is TokenType.OpOpenBrace or TokenType.OpCloseBrace or
			TokenType.OpFatArrow or TokenType.OpArrow or TokenType.OpEqual || StartsMemberDeclaration(index));
	
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
		var mark = KeywordMark;
		var first = Tokens[index];
		var isRef = Match(ref index, TokenType.KeywordRef);
		if (isRef && !Peek(index, TokenType.KeywordFun))
			throw Expected(index, "'fun'");
		
		var isExternal = !isRef && Match(ref index, _externalKeywords, TokenType.KeywordExt);
		if (!Match(ref index, out var keyword, TokenType.KeywordFun))
		{
			index = start;
			RewindKeywords(mark);
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
		if (ParseDeclaredParameter is { } parseDeclared && Declared is { } declared &&
		    Peek(index, TokenType.Identifier) && Peek(index + 1, TokenType.OpColon) &&
		    Tokens[index + 1].Line == Tokens[index].Line)
		{
			var parameter = parseDeclared(ref index);
			declared.Add(parameter);
			return new DeclaredArgumentNode(parameter.Identifier);
		}
		
		if (Peek(index, TokenType.KeywordImm) || Peek(index, TokenType.KeywordMut) || Peek(index, TokenType.KeywordDyn))
			return new TypeArgumentNode(ParseType(ref index));
		
		if (TryParseFunctionType(ref index) is { } functionType)
			return new TypeArgumentNode(functionType);
		
		if (!Peek(index, TokenType.KeywordDrop) && !new ExpressionParser(Tokens).CanStartExpression(index))
			throw Expected(index, "a type or a value");
		
		var expression = ParseExpression(ref index);
		if (expression is VarExpressionNode var)
			return new IdentifierArgumentNode(var.Identifier);
		
		return new ExpressionArgumentNode(expression);
	}
	
	private IExpressionNode ParseExpression(ref int index) =>
		new ExpressionParser(Tokens, ContextualKeywords).Parse(ref index);
}