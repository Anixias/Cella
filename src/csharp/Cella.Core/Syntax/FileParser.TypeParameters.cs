using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private List<TypeParameterNode>? ParseTypeParameters(ref int index, bool startsDeclaration = false) =>
		ParseTypeParameters(ref index, startsDeclaration, out var isPartial) is { } parameters && !isPartial
			? parameters
			: null;
	
	private List<TypeParameterNode>? ParseTypeParameters(ref int index, bool startsDeclaration, out bool isPartial)
	{
		isPartial = false;
		var parameters = new List<TypeParameterNode>();
		if (!startsDeclaration && !IsOnSameLine(index) ||
		    !Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			return parameters;
		
		do
		{
			if (!Match(ref index, out var name, TokenType.Identifier))
			{
				if (IsReservedWord(index))
					Report(Tokens[index], DescribeReservedWord(Tokens[index]));
				else
					ReportExpected(index, "a type parameter name");
				
				isPartial = !IsOnSameLine(index);
				return isPartial ? parameters : null;
			}
			
			if (IsOnSameLine(index) && Peek(index, TokenType.OpOpenBracket))
			{
				if (ParseTypeParameters(ref index, false, out var nestedPartial) is not { } nested)
					return null;
				
				parameters.Add(new(name, [], [], []) { Nested = [..nested] });
				if (nestedPartial)
				{
					isPartial = true;
					return parameters;
				}
				
				continue;
			}
			
			var keywords = new List<Token>();
			var traits = new List<ITypeNode>();
			var constructors = new List<ConstructorConstraintNode>();
			var declared = new List<TypeParameterNode>();
			ITypeNode? valueType = null;
			var hasConstraints = Match(ref index, TokenType.OpColon);
			if (hasConstraints && Match(ref index, TokenType.KeywordConst)
				    ? (valueType = ParseConstraintType(ref index)) is null
				    : hasConstraints && !ParseConstraints(ref index, keywords, traits, constructors, false, declared))
			{
				if (IsOnSameLine(index))
					return null;
				
				parameters.Add(new(name, keywords, traits, constructors) { Declared = [..declared] });
				isPartial = true;
				return parameters;
			}
			
			ITypeNode? defaultType = null;
			IExpressionNode? defaultValue = null;
			var hasDefault = Match(ref index, TokenType.OpEqual);
			if (hasDefault && EndsListItem(index, TokenType.OpCloseBracket))
			{
				ReportExpected(index - 1, valueType is null ? "a type" : "an expression");
				hasDefault = false;
			}
			
			if (hasDefault &&
			    (valueType is null && (defaultType = ParseConstraintType(ref index)) is null ||
			     valueType is not null && (defaultValue = ParseDefaultValue(ref index)) is null))
			{
				if (IsOnSameLine(index))
					return null;
				
				parameters.Add(new(name, keywords, traits, constructors)
				{
					ValueType = valueType,
					Declared = [..declared]
				});
				
				isPartial = true;
				return parameters;
			}
			
			parameters.Add(new(name, keywords, traits, constructors)
			{
				ValueType = valueType,
				DefaultType = defaultType,
				DefaultValue = defaultValue,
				Declared = [..declared]
			});
		} while (MatchSeparator(ref index, TokenType.OpCloseBracket));
		
		if (Match(ref index, TokenType.OpCloseBracket))
			return parameters;
		
		ReportExpected(index, "',' or ']'", openBracket);
		isPartial = !IsOnSameLine(index);
		return isPartial ? parameters : null;
	}
	
	private bool ParseConstraints(ref int index, List<Token> keywords, List<ITypeNode> traits,
		List<ConstructorConstraintNode> constructors, bool isOnOneLine, List<TypeParameterNode>? declared = null)
	{
		do
		{
			if (Match(ref index, out var newKeyword, TokenType.KeywordNew))
			{
				if (ParseConstructorConstraint(ref index, newKeyword) is not { } constructor)
					return false;
				
				constructors.Add(constructor);
			}
			else if (!StartsExternalFunctionType(index) &&
			         (Match(ref index, out var keyword, _constraintKeywords, _constraintTypes) ||
			          Match(ref index, out keyword, TokenType.KeywordNull) ||
			          Match(ref index, out keyword, TokenType.KeywordAtomic)))
			{
				keywords.Add(keyword);
				if (keyword.Type == TokenType.KeywordDrop && Peek(index, TokenType.OpOpenParen) &&
				    Peek(index + 1, TokenType.OpCloseParen))
				{
					index += 2;
					ReportEmptyParentheses(index - 2, index);
				}
			}
			else if (Peek(index, TokenType.KeywordDyn))
			{
				Report(Tokens[index], "Cannot use 'dyn' types as constraints");
				return false;
			}
			else if (!Peek(index, TokenType.Identifier) && !Peek(index, TokenType.KeywordFun) &&
			         !Peek(index, TokenType.KeywordRef))
			{
				ReportExpected(index, "a constraint");
				return Peek(index, TokenType.OpCloseBracket) || Peek(index, TokenType.OpComma);
			}
			else if (ParseConstraintType(ref index, declared) is { } trait)
				traits.Add(trait);
			else
				return false;
		} while (isOnOneLine ? MatchOnLine(ref index, TokenType.OpPlus) : Match(ref index, TokenType.OpPlus));
		
		return true;
	}
	
	private ConstructorConstraintNode? ParseConstructorConstraint(ref int index, Token keyword)
	{
		var modes = new List<Token?>();
		var types = new List<ITypeNode>();
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
			return new(keyword, modes, types, keyword);
		
		if (Match(ref index, out var closeParen, TokenType.OpCloseParen))
		{
			ReportEmptyParentheses(index - 2, index);
			return new(keyword, modes, types, closeParen);
		}
		
		do
		{
			var isBorrowType = (Peek(index, TokenType.KeywordMut) || Peek(index, TokenType.KeywordImm)) &&
			                   Peek(index + 1, TokenType.OpOpenBracket);
			
			modes.Add(!isBorrowType && Match(ref index, out var mode, TokenSets.ParameterModes) ? mode : null);
			if (!StartsType(index))
			{
				ReportExpected(index, "a type");
				return null;
			}
			
			if (ParseConstraintType(ref index) is not { } type)
				return null;
			
			types.Add(type);
		} while (MatchSeparator(ref index, TokenType.OpCloseParen));
		
		if (Match(ref index, out closeParen, TokenType.OpCloseParen))
			return new(keyword, modes, types, closeParen);
		
		ReportExpected(index, "',' or ')'", openParen);
		return null;
	}
	
	private bool StartsType(int index) => Peek(index, TokenType.Identifier) || Peek(index, TokenType.KeywordImm) ||
	                                      Peek(index, TokenType.KeywordMut) || Peek(index, TokenType.KeywordDyn) ||
	                                      Peek(index, TokenType.KeywordFun) || Peek(index, TokenType.KeywordRef);
	
	private ITypeNode? ParseConstraintType(ref int index, List<TypeParameterNode>? declared = null)
	{
		try
		{
			return declared is null
				? ParseType(ref index)
				: new TypeParser(Tokens, ContextualKeywords)
				{
					ReportRecovered = Diagnostics.Add,
					ParseDeclaredParameter = ParseDeclaredParameter,
					Declared = declared
				}.Parse(ref index);
		}
		catch (ParseException e)
		{
			AddParseError(e);
		}
		
		return null;
	}
	
	private TypeParameterNode ParseDeclaredParameter(ref int index)
	{
		var name = Tokens[index];
		index += 2;
		var keywords = new List<Token>();
		var traits = new List<ITypeNode>();
		var constructors = new List<ConstructorConstraintNode>();
		var declared = new List<TypeParameterNode>();
		var reported = Diagnostics.Count;
		if (ParseConstraints(ref index, keywords, traits, constructors, false, declared))
			return new(name, keywords, traits, constructors) { Declared = [..declared] };
		
		if (Diagnostics.Count == reported)
			ReportExpected(index, "a constraint");
		
		throw new ParseException(Diagnostics.Last());
	}
	
	private IExpressionNode? ParseDefaultValue(ref int index)
	{
		try
		{
			return ParseExpression(ref index);
		}
		catch (ParseException e)
		{
			AddParseError(e);
		}
		
		return null;
	}
	
	private string? DescribeTypeParameterError(int index, bool isExternal)
	{
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordEnum))
			return isExternal ? "Cannot declare type parameters on ext enums" : null;
		
		if (Match(ref index, TokenType.KeywordFun))
			return isExternal ? "Cannot declare type parameters on ext functions" : null;
		
		return Match(ref index, _bindingKeywords) ? "Cannot declare type parameters on globals" : null;
	}
	
	private bool RejectTypeParameters(ref int index, string kind)
	{
		if (ParseTypeParameters(ref index) is not { } typeParameters)
			return false;
		
		if (typeParameters is [var first, ..])
			Report(first.SourceLocation, $"Cannot declare type parameters on {kind}");
		
		return true;
	}
}