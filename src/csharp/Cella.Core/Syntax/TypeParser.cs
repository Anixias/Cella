using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed class TypeParser(ImmutableArray<Token> tokens) : BaseParser<ITypeNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _functionKeywords =
		new[] { TokenType.KeywordExt, TokenType.KeywordFun }.ToDictionary(static t => t.Representation);
	
	private static readonly HashSet<TokenType> _parameterModes = [TokenType.KeywordMut, TokenType.KeywordOwn];
	
	private static readonly Dictionary<string, TokenType> _parameterModeKeywords =
		_parameterModes.ToDictionary(static t => t.Representation);
	
	public override ITypeNode Parse(ref int index) => ParseType(ref index);
	
	private ITypeNode ParseType(ref int index)
	{
		if (TryParseFunctionType(ref index) is { } functionType)
			return functionType;
		
		// TEMP Should emit an erroneous node instead
		if (!Match(ref index, out var identifier, TokenType.Identifier))
			throw new InvalidOperationException();
		
		if (Peek(index, TokenType.OpDot))
			return ParseQualifiedType(ref index, identifier);
		
		var startIndex = index;
		if (!Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			return new IdentifierTypeNode(identifier);
		
		// Disallow the open bracket being on another line from the identifier; backtrack to unconsume open bracket
		if (openBracket.Line != identifier.Line)
		{
			index = startIndex;
			return new IdentifierTypeNode(identifier);
		}
		
		var args = new List<IGenericArgumentNode> { ParseGenericArgument(ref index) };
		while (Match(ref index, TokenType.OpComma))
			args.Add(ParseGenericArgument(ref index));
		
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw new InvalidOperationException();
		
		var (source, range) = identifier.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		
		return new GenericTypeNode(new(source, range), identifier, args);
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
		var isExternal = Match(ref index, _functionKeywords, TokenType.KeywordExt);
		if (!Match(ref index, out var end, _functionKeywords, TokenType.KeywordFun))
		{
			index = start;
			return null;
		}
		
		var parameterModes = new List<Token?>();
		var parameterTypes = new List<ITypeNode>();
		if (Match(ref index, TokenType.OpOpenParen) && !Match(ref index, out end, TokenType.OpCloseParen))
		{
			do
			{
				parameterModes.Add(ParseParameterMode(ref index));
				parameterTypes.Add(ParseType(ref index));
			} while (Match(ref index, TokenType.OpComma));
			
			if (!Match(ref index, out end, TokenType.OpCloseParen))
				throw new InvalidOperationException();
		}
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
		var (source, range) = first.SourceLocation;
		range = range.Join((returnType?.SourceLocation ?? end.SourceLocation).Range);
		return new FunctionTypeNode(new(source, range), isExternal, parameterModes, parameterTypes, returnType);
	}
	
	private Token? ParseParameterMode(ref int index)
	{
		var typeIndex = index;
		if (!Match(ref typeIndex, out var mode, _parameterModeKeywords, _parameterModes) ||
		    !Peek(typeIndex, TokenType.Identifier))
			return null;
		
		index = typeIndex;
		return mode;
	}
	
	private IGenericArgumentNode ParseGenericArgument(ref int index)
	{
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