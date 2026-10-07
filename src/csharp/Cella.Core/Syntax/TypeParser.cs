using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed class TypeParser(ImmutableArray<Token> tokens) : BaseParser<ITypeNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _externalKeywords =
		new[] { TokenType.KeywordExt }.ToDictionary(static t => t.Representation);
	
	private static readonly HashSet<TokenType> _parameterModes = [TokenType.KeywordMut, TokenType.KeywordOwn];
	private static readonly HashSet<TokenType> _borrowKeywords = [TokenType.KeywordImm, TokenType.KeywordMut];
	
	public override ITypeNode Parse(ref int index) => ParseType(ref index);
	
	private ITypeNode ParseType(ref int index)
	{
		if (Match(ref index, out var borrowKeyword, _borrowKeywords))
			return ParseBorrowType(ref index, borrowKeyword);
		
		if (Match(ref index, out var dynKeyword, TokenType.KeywordDyn))
			return ParseDynType(ref index, dynKeyword);
		
		if (TryParseFunctionType(ref index) is { } functionType)
			return functionType;
		
		// TEMP Should emit an erroneous node instead
		if (!Match(ref index, out var identifier, TokenType.Identifier))
			throw new InvalidOperationException();
		
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
		while (Match(ref index, TokenType.OpComma))
			args.Add(ParseGenericArgument(ref index));
		
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw new InvalidOperationException();
		
		var (source, range) = identifier.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		
		return new GenericTypeNode(new(source, range), name, args)
		{
			Qualifiers = qualified is null ? [] : qualified.Parts[..^1]
		};
	}
	
	private BorrowTypeNode ParseBorrowType(ref int index, Token keyword)
	{
		if (!Match(ref index, TokenType.OpOpenBracket))
			throw Expected(index, "'['");
		
		var target = ParseType(ref index);
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw Expected(index, "']'");
		
		return new BorrowTypeNode(keyword, target, closeBracket);
	}
	
	private DynTypeNode ParseDynType(ref int index, Token keyword)
	{
		if (!Match(ref index, TokenType.OpOpenBracket))
			throw Expected(index, "'['");
		
		if (!Peek(index, TokenType.Identifier))
			throw Expected(index, "a trait");
		
		var trait = ParseType(ref index);
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw Expected(index, "']'");
		
		return new DynTypeNode(keyword, trait, closeBracket);
	}
	
	private ParseException Expected(int index, string expected)
	{
		var token = Tokens[Math.Min(index, Tokens.Length - 1)];
		return new(new Diagnostic(DiagnosticSeverity.Error, token.SourceLocation, $"Expected {expected}"));
	}
	
	private QualifiedTypeNode ParseQualifiedType(ref int index, Token first)
	{
		var parts = new List<Token> { first };
		while (Match(ref index, TokenType.OpDot))
		{
			if (!Match(ref index, out var part, TokenType.Identifier))
				throw new InvalidOperationException();
			
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
		var isExternal = Match(ref index, _externalKeywords, TokenType.KeywordExt);
		if (!Match(ref index, TokenType.KeywordFun))
		{
			index = start;
			return null;
		}
		
		if (!Match(ref index, TokenType.OpOpenBracket))
			throw Expected(index, "'['");
		
		var parameterModes = new List<Token?>();
		var parameterTypes = new List<ITypeNode>();
		if (!Peek(index, TokenType.OpArrow) && !Peek(index, TokenType.OpCloseBracket))
		{
			do
			{
				var isBorrowType = Peek(index, TokenType.KeywordMut) && Peek(index + 1, TokenType.OpOpenBracket);
				parameterModes.Add(!isBorrowType && Match(ref index, out var mode, _parameterModes) ? mode : null);
				parameterTypes.Add(ParseType(ref index));
			} while (Match(ref index, TokenType.OpComma));
		}
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw Expected(index, "']'");
		
		var (source, range) = first.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		return new FunctionTypeNode(new(source, range), isExternal, parameterModes, parameterTypes, returnType);
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
		catch
		{
			return null;
		}
	}
}