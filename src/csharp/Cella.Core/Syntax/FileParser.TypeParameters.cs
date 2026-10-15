using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private List<TypeParameterNode>? ParseTypeParameters(ref int index)
	{
		var parameters = new List<TypeParameterNode>();
		if (!IsOnSameLine(index) || !Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			return parameters;
		
		do
		{
			if (!Match(ref index, out var name, TokenType.Identifier))
			{
				if (IsReservedWord(index))
					Report(Tokens[index], DescribeReservedWord(Tokens[index]));
				else
					ReportExpected(index, "a type parameter name");
				
				return null;
			}
			
			var keywords = new List<Token>();
			var traits = new List<ITypeNode>();
			var constructors = new List<ConstructorConstraintNode>();
			ITypeNode? valueType = null;
			var hasConstraints = Match(ref index, TokenType.OpColon);
			if (hasConstraints && Match(ref index, TokenType.KeywordVal))
			{
				if ((valueType = ParseConstraintType(ref index)) is null)
					return null;
			}
			else if (hasConstraints)
			{
				do
				{
					if (Match(ref index, out var newKeyword, TokenType.KeywordNew))
					{
						if (ParseConstructorConstraint(ref index, newKeyword) is not { } constructor)
							return null;
						
						constructors.Add(constructor);
					}
					else if (!StartsExternalFunctionType(index) &&
					         (Match(ref index, out var keyword, _constraintKeywords, _constraintTypes) ||
					          Match(ref index, out keyword, TokenType.KeywordNull) ||
					          Match(ref index, out keyword, TokenType.KeywordAtomic)))
						keywords.Add(keyword);
					else if (Peek(index, TokenType.KeywordDyn))
					{
						Report(Tokens[index], "Cannot use 'dyn' types as constraints");
						return null;
					}
					else if (!Peek(index, TokenType.Identifier) && !Peek(index, TokenType.KeywordFun) &&
					         !Peek(index, TokenType.KeywordRef))
					{
						ReportExpected(index, "a constraint");
						return null;
					}
					else if (ParseConstraintType(ref index) is { } trait)
						traits.Add(trait);
					else
						return null;
				} while (Match(ref index, TokenType.OpPlus));
			}
			
			parameters.Add(new(name, keywords, traits, constructors) { ValueType = valueType });
		} while (Match(ref index, TokenType.OpComma));
		
		if (Match(ref index, TokenType.OpCloseBracket))
			return parameters;
		
		ReportExpected(index, "',' or ']'", openBracket);
		return null;
	}
	
	private ConstructorConstraintNode? ParseConstructorConstraint(ref int index, Token keyword)
	{
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			ReportExpected(index, "'('");
			return null;
		}
		
		var modes = new List<Token?>();
		var types = new List<ITypeNode>();
		if (Match(ref index, out var closeParen, TokenType.OpCloseParen))
			return new(keyword, modes, types, closeParen);
		
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
		} while (Match(ref index, TokenType.OpComma));
		
		if (Match(ref index, out closeParen, TokenType.OpCloseParen))
			return new(keyword, modes, types, closeParen);
		
		ReportExpected(index, "',' or ')'", openParen);
		return null;
	}
	
	private bool StartsType(int index) => Peek(index, TokenType.Identifier) || Peek(index, TokenType.KeywordImm) ||
	                                      Peek(index, TokenType.KeywordMut) || Peek(index, TokenType.KeywordDyn) ||
	                                      Peek(index, TokenType.KeywordFun) || Peek(index, TokenType.KeywordRef);
	
	private ITypeNode? ParseConstraintType(ref int index)
	{
		try
		{
			return ParseType(ref index);
		}
		catch (ParseException e)
		{
			Diagnostics.Add(e.Diagnostic);
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