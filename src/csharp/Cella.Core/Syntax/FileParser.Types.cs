using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private RecordNode? ParseRecord(ref int index, Token identifier, DeclarationModifiers modifiers, bool isRef,
		bool isUnion, List<TypeParameterNode> typeParameters)
	{
		// When this is called, the identifier and rec keyword are already consumed
		// Caller is expected to resync in case of errors
		
		if (ParseTraitList(ref index, TokenType.KeywordImpl) is not { } traits)
			return null;
		
		var members = new List<IDeclarationNode>();
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace) &&
		    !ParseMembers(ref index, openBrace, null, members))
			return null;
		
		return new(identifier, modifiers.Tokens, isRef, members)
		{
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			Traits = [..traits],
			IsUnion = isUnion
		};
	}
	
	private ITypeNode ParseImplTarget(ref int index, Token identifier)
	{
		if (!Peek(index, TokenType.OpDot))
			return new IdentifierTypeNode(identifier);
		
		var parts = new List<Token> { identifier };
		while (IsOnSameLine(index) && Peek(index, TokenType.OpDot) && Peek(index + 1, TokenType.Identifier))
		{
			index++;
			parts.Add(Tokens[index++]);
		}
		
		var (source, range) = identifier.SourceLocation;
		return new QualifiedTypeNode(new(source, range.Join(parts[^1].SourceLocation.Range)), parts);
	}
	
	private List<ITypeNode>? ParseTraitList(ref int index, TokenType keyword)
	{
		var traits = new List<ITypeNode>();
		if (!IsOnSameLine(index) || !Match(ref index, _traitListKeywords, keyword))
			return traits;
		
		return ParseTraitNames(ref index, traits) ? traits : null;
	}
	
	private bool ParseTraitNames(ref int index, List<ITypeNode> traits)
	{
		do
		{
			if (!Peek(index, TokenType.Identifier))
			{
				ReportExpected(index, "a trait");
				return false;
			}
			
			traits.Add(ParseType(ref index));
		} while (Match(ref index, TokenType.OpPlus));
		
		if (!IsOnSameLine(index) || !Peek(index, TokenType.OpComma) && !Peek(index, TokenType.Identifier))
			return true;
		
		ReportExpected(index, "'+'");
		return false;
	}
	
	private TraitNode? ParseTrait(ref int index, Token identifier, DeclarationModifiers modifiers,
		List<TypeParameterNode> typeParameters)
	{
		var implIndex = index;
		if (IsOnSameLine(index) && Match(ref implIndex, out var implKeyword, _traitListKeywords, TokenType.KeywordImpl))
		{
			Report(implKeyword, "Expected 'req' or '{'");
			return null;
		}
		
		if (ParseTraitList(ref index, TokenType.KeywordReq) is not { } requiredTraits)
			return null;
		
		var members = new List<IDeclarationNode>();
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace))
		{
			var outer = allowsMissingBodies;
			allowsMissingBodies = true;
			var parsed = ParseMembers(ref index, openBrace, null, members);
			allowsMissingBodies = outer;
			if (!parsed)
				return null;
		}
		
		return new(identifier, modifiers.Tokens, members)
		{
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			RequiredTraits = [..requiredTraits]
		};
	}
	
	private ImplNode? ParseImpl(ref int index, ITypeNode target, Token keyword, List<TypeParameterNode> typeParameters)
	{
		var traits = new List<ITypeNode>();
		if (!Peek(index, TokenType.OpOpenBrace))
		{
			if (!Peek(index, TokenType.Identifier))
			{
				ReportExpected(index, "a trait or '{'");
				return null;
			}
			
			if (!ParseTraitNames(ref index, traits))
				return null;
		}
		
		var members = new List<IDeclarationNode>();
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace) &&
		    !ParseMembers(ref index, openBrace, null, members))
			return null;
		
		return new(target, keyword, traits, members) { TypeParameters = [..typeParameters] };
	}
	
	private bool ParseMembers(ref int index, Token openBrace, Token? block, List<IDeclarationNode> members)
	{
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				ReportUnclosed(openBrace);
				return false;
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref i, open, isActive) =>
					    ParseMembers(ref i, open, block, isActive ? members : [])))
					return false;
				
				continue;
			}
			
			if (MatchPubBlock(ref index, _memberContextualKeywords, out var pubKeyword, out var pubBrace))
			{
				Report(pubKeyword, "Cannot declare 'pub' members");
				if (!ParseMembers(ref index, pubBrace, block, members))
					return false;
				
				continue;
			}
			
			if (MatchVisibilityBlock(ref index, _memberContextualKeywords, out var keyword, out var blockBrace))
			{
				if (block is { } outer)
					Report(keyword, $"Cannot use '{keyword.Text}' in '{outer.Text}' blocks");
				
				if (!ParseMembers(ref index, blockBrace, block ?? keyword, members))
					return false;
				
				continue;
			}
			
			// TODO Diagnostics
			if (ParseMember(ref index, block) is not { } member)
			{
				SkipToEnd(ref index);
				return false;
			}
			
			members.Add(member);
		}
		
		return true;
	}
	
	private EnumNode? ParseEnum(ref int index, Token identifier, DeclarationModifiers modifiers, bool isExternal,
		bool isRef, List<TypeParameterNode> typeParameters)
	{
		ITypeNode? tagType = null;
		ITypeNode? matchedType = null;
		if (Match(ref index, out var matchKeyword, TokenType.KeywordMatch))
		{
			if (isExternal)
				Report(matchKeyword, "Cannot use 'match' on ext enums");
			
			matchedType = ParseType(ref index);
		}
		else if (Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			tagType = ParseType(ref index);
			if (!Match(ref index, TokenType.OpCloseParen))
			{
				ReportExpected(index, "')'", openParen);
				return null;
			}
			
			if (Peek(index, TokenType.KeywordMatch))
			{
				Report(Tokens[index], "Cannot use 'match' on enums with a tag type");
				return null;
			}
		}
		
		if (ParseTraitList(ref index, TokenType.KeywordImpl) is not { } traits)
			return null;
		
		if (!Peek(index, TokenType.OpOpenBrace) && !AtEnd(index) &&
		    (IsOnSameLine(index) || Peek(index, TokenType.Identifier) && !Peek(index + 1, TokenType.OpColon) &&
			    !Peek(index + 1, TokenType.OpOpenBracket)))
		{
			ReportMissing(index, "'{'");
			return null;
		}
		
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			return new(identifier, modifiers.Tokens, isExternal, isRef, tagType, [], [])
			{
				Visibility = modifiers.Visibility,
				TypeParameters = [..typeParameters],
				MatchedType = matchedType,
				Traits = [..traits]
			};
		
		var cases = new List<EnumCaseNode>();
		var members = new List<IDeclarationNode>();
		if (!ParseEnumBody(ref index, openBrace, null, matchedType, cases, members))
			return null;
		
		return new(identifier, modifiers.Tokens, isExternal, isRef, tagType, cases, members)
		{
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			MatchedType = matchedType,
			Traits = [..traits]
		};
	}
	
	private bool ParseEnumBody(ref int index, Token openBrace, Token? block, ITypeNode? matchedType,
		List<EnumCaseNode> cases, List<IDeclarationNode> members)
	{
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				ReportUnclosed(openBrace);
				return false;
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref i, open, isActive) => isActive
					    ? ParseEnumBody(ref i, open, block, matchedType, cases, members)
					    : ParseEnumBody(ref i, open, block, matchedType, [], [])))
					return false;
				
				continue;
			}
			
			if (MatchPubBlock(ref index, _memberContextualKeywords, out var pubKeyword, out var pubBrace))
			{
				Report(pubKeyword, "Cannot declare 'pub' members");
				if (!ParseEnumBody(ref index, pubBrace, block, matchedType, cases, members))
					return false;
				
				continue;
			}
			
			if (MatchVisibilityBlock(ref index, _memberContextualKeywords, out var keyword, out var blockBrace))
			{
				if (block is { } outer)
					Report(keyword, $"Cannot use '{keyword.Text}' in '{outer.Text}' blocks");
				
				if (!ParseEnumBody(ref index, blockBrace, block ?? keyword, matchedType, cases, members))
					return false;
				
				continue;
			}
			
			if (MatchOperatorName(ref index, out var symbol))
			{
				if (ParseOperatorMember(ref index, symbol, block) is not { } operatorMember)
					return false;
				
				members.Add(operatorMember);
				continue;
			}
			
			if (Match(ref index, out var lifecycleKeyword, _lifecycleKeywords))
			{
				var isConversion = lifecycleKeyword.Type == TokenType.KeywordNew && StartsConversion(index);
				if (!isConversion)
					Report(lifecycleKeyword, $"Cannot declare {DescribeLifecycle(lifecycleKeyword)} in enums");
				
				if (ParseLifecycleMember(ref index, lifecycleKeyword, block) is not { } lifecycleMember)
					return false;
				
				if (isConversion)
					members.Add(lifecycleMember);
				
				continue;
			}
			
			if (StartsEnumMember(index))
			{
				if (ParseEnumMember(ref index, block) is not { } member)
					return false;
				
				members.Add(member);
				continue;
			}
			
			var caseStart = index;
			if (Match(ref index, out var caseVisibility, _memberContextualKeywords, _visibilityKeywords) &&
			    Peek(index, TokenType.Identifier) && IsOnSameLine(index))
				Report(caseVisibility, $"Cannot declare '{caseVisibility.Text}' cases");
			else
				index = caseStart;
			
			if (ParseEnumCase(ref index, matchedType) is not { } enumCase)
				return false;
			
			if (block is { } caseBlock)
				Report(enumCase.Identifier, $"Cannot declare '{caseBlock.Text}' cases");
			
			cases.Add(enumCase);
		}
		
		return true;
	}
	
	private bool StartsEnumMember(int index) => Peek(index, TokenType.Identifier) &&
	                                            (Peek(index + 1, TokenType.OpColon) ||
	                                             Peek(index + 1, TokenType.OpOpenBracket) && IsOnSameLine(index + 1));
	
	private IDeclarationNode? ParseEnumMember(ref int index, Token? block)
	{
		var identifier = Tokens[index++];
		if (ParseTypeParameters(ref index) is not { } typeParameters || !Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseMemberModifiers(ref index, block);
		if (!IsFunctionMember(index))
			RejectMemberTypeParameters(typeParameters, index);
		
		if (Match(ref index, out var bindingKeyword, _bindingKeywords))
			return ParseStaticField(ref index, identifier, modifiers, bindingKeyword);
		
		if (StartsProperty(index))
			return ParseDeclaration(ref index, identifier, "property",
				(ref i) => ParseProperty(ref i, identifier, modifiers));
		
		if (!IsFunctionMember(index))
		{
			Report(identifier, "Cannot declare fields in enums");
			return null;
		}
		
		RejectWriteRestriction(modifiers, "functions");
		Match(ref index, TokenType.KeywordFun);
		return ParseDeclaration(ref index, identifier, "function",
			(ref i) => ParseFunction(ref i, identifier, modifiers, false, typeParameters));
	}
	
	private EnumCaseNode? ParseEnumCase(ref int index, ITypeNode? matchedType)
	{
		if (Tokens[index] is { Type: TokenType.Identifier, Text: "_" } ||
		    !Match(ref index, out var name, TokenType.Identifier))
		{
			ReportExpected(index, "a case name");
			return null;
		}
		
		var payload = new List<FieldNode>();
		if (Tokens[index].Line == name.Line && Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			var parsed = matchedType is null
				? ParsePayload(ref index, openParen, payload)
				: ParseMatchedPayload(ref index, openParen, matchedType, payload);
			
			if (!parsed)
				return null;
		}
		
		var values = new List<IExpressionNode>();
		Token? elseKeyword = null;
		if (Match(ref index, TokenType.OpEqual))
		{
			if (Match(ref index, out var keyword, TokenType.KeywordElse))
				elseKeyword = keyword;
			else
				values.AddRange(ParseCaseValues(ref index));
		}
		
		return new(name, payload, values, elseKeyword);
	}
	
	private List<IExpressionNode> ParseCaseValues(ref int index)
	{
		var values = new List<IExpressionNode> { ParseExpression(ref index) };
		while (Match(ref index, TokenType.OpComma))
			values.Add(ParseExpression(ref index));
		
		return values;
	}
	
	private bool ParsePayload(ref int index, Token openParen, List<FieldNode> payload)
	{
		do
		{
			if (!Match(ref index, out var fieldName, TokenType.Identifier))
			{
				ReportExpected(index, "a payload name");
				return false;
			}
			
			if (!Match(ref index, TokenType.OpColon))
			{
				ReportExpected(index, "':' and a type");
				return false;
			}
			
			if (Tokens[index].Text == "req" && StartsMemberType(index + 1))
				Report(Tokens[index++], "Cannot declare required fields in enums");
			
			payload.Add(new FieldNode(fieldName, ParseType(ref index), []));
		} while (Match(ref index, TokenType.OpComma));
		
		if (Match(ref index, TokenType.OpCloseParen))
			return true;
		
		ReportExpected(index, "',' or ')'", openParen);
		return false;
	}
	
	private bool ParseMatchedPayload(ref int index, Token openParen, ITypeNode matchedType, List<FieldNode> payload)
	{
		if (!Match(ref index, out var fieldName, TokenType.Identifier))
		{
			ReportExpected(index, "a payload name");
			return false;
		}
		
		payload.Add(new FieldNode(fieldName, matchedType, []));
		if (Match(ref index, TokenType.OpCloseParen))
			return true;
		
		ReportExpected(index, "')'", openParen);
		return false;
	}
}
