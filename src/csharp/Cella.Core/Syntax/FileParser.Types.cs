using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private RecordNode? ParseRecord(ref int index, Token identifier, DeclarationModifiers modifiers, bool isRef,
		bool isOwn, bool isUnion, List<TypeParameterNode> typeParameters)
	{
		// When this is called, the identifier and rec keyword are already consumed
		// Caller is expected to resync in case of errors
		
		if (ParseTraitList(ref index) is not { } traits)
			return null;
		
		if (MissesBody(index))
		{
			ReportMissing(index, "'{'");
			if (!IsOnSameLine(index))
				return null;
			
			SkipToLineEnd(ref index);
		}
		
		var members = new List<IDeclarationNode>();
		var lastHeaderToken = Tokens[index - 1];
		var hasBody = Match(ref index, out var openBrace, TokenType.OpOpenBrace);
		if (hasBody && !ParseMembers(ref index, openBrace, null, members))
			return null;
		
		return new(identifier, modifiers.Tokens, isRef, members)
		{
			Extent = Span(identifier, Tokens[index - 1]),
			Body = hasBody ? Span(openBrace, Tokens[index - 1]) : null,
			LastHeaderToken = lastHeaderToken,
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			Traits = [..traits],
			ConstantTraits = ConstantsOf(traits),
			IsUnion = isUnion,
			IsOwn = isOwn
		};
	}
	
	private void SkipToLineEnd(ref int index)
	{
		while (IsOnSameLine(index) && !Peek(index, TokenType.OpOpenBrace))
			index++;
	}
	
	private bool MissesBody(int index) =>
		!Peek(index, TokenType.OpOpenBrace) && !AtEnd(index) &&
		(IsOnSameLine(index) || Peek(index, TokenType.Identifier) && !Peek(index + 1, TokenType.OpColon) &&
			!Peek(index + 1, TokenType.OpOpenBracket));
	
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
		
		if (parts is [_])
			return new IdentifierTypeNode(identifier);
		
		var (source, range) = identifier.SourceLocation;
		return new QualifiedTypeNode(new(source, range.Join(parts[^1].SourceLocation.Range)), parts);
	}
	
	private ImmutableArray<ITypeNode> ConstantsOf(IEnumerable<ITypeNode> traits) =>
		[..traits.Where(_constantTraits.Contains)];
	
	private List<ITypeNode>? ParseTraitList(ref int index)
	{
		var traits = new List<ITypeNode>();
		if (!IsOnSameLine(index) || !Match(ref index, _traitListKeywords, TokenType.KeywordImpl))
			return traits;
		
		return ParseTraitNames(ref index, traits) || Peek(index, TokenType.OpOpenBrace) ? traits : null;
	}
	
	private bool ParseTraitNames(ref int index, List<ITypeNode> traits)
	{
		do
		{
			var isConstant = Match(ref index, TokenType.KeywordConst);
			if (!Peek(index, TokenType.Identifier))
			{
				ReportExpected(index, "a trait");
				return false;
			}
			
			var trait = ParseType(ref index);
			traits.Add(trait);
			if (isConstant)
				_constantTraits.Add(trait);
		} while (MatchOnLine(ref index, TokenType.OpPlus));
		
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
		
		var keywords = new List<Token>();
		var traits = new List<ITypeNode>();
		var constructors = new List<ConstructorConstraintNode>();
		if (IsOnSameLine(index) && Match(ref index, _traitListKeywords, TokenType.KeywordReq))
		{
			if (!ParseConstraints(ref index, keywords, traits, constructors, true) &&
			    !Peek(index, TokenType.OpOpenBrace))
				return null;
			
			if (IsOnSameLine(index) && (Peek(index, TokenType.OpComma) || Peek(index, TokenType.Identifier)))
			{
				ReportExpected(index, "'+'");
				return null;
			}
		}
		
		if (IsOnSameLine(index) && !Peek(index, TokenType.OpOpenBrace))
		{
			ReportExpected(index, "'{'");
			SkipToLineEnd(ref index);
		}
		
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
			Extent = Span(identifier, Tokens[index - 1]),
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			Requirements = new(identifier, keywords, traits, constructors)
		};
	}
	
	private ImplNode? ParseImpl(ref int index, ITypeNode target, Token keyword, List<TypeParameterNode> typeParameters,
		DeclarationModifiers modifiers)
	{
		var traits = new List<ITypeNode>();
		if (!Peek(index, TokenType.OpOpenBrace))
		{
			if (!Peek(index, TokenType.Identifier) && !Peek(index, TokenType.KeywordConst))
			{
				ReportExpected(index, "a trait or '{'");
				return null;
			}
			
			if (!ParseTraitNames(ref index, traits) && !Peek(index, TokenType.OpOpenBrace))
				return null;
		}
		
		if (traits.Count > 0 && modifiers.Visibility is { } visibility)
			Report(modifiers.Tokens is [var written, ..] ? written : keyword,
				$"Cannot use '{visibility.Text}' on trait impls");
		
		var members = new List<IDeclarationNode>();
		var outerPublic = allowsPublicMembers;
		allowsPublicMembers = traits.Count == 0;
		var lastHeaderToken = Tokens[index - 1];
		var hasBody = Match(ref index, out var openBrace, TokenType.OpOpenBrace);
		var parsed = !hasBody ||
		             ParseMembers(ref index, openBrace, traits.Count == 0 ? modifiers.Visibility : null, members);
		
		allowsPublicMembers = outerPublic;
		if (!parsed)
			return null;
		
		return new(target, keyword, traits, members)
		{
			ConstantTraits = ConstantsOf(traits),
			TypeParameters = [..typeParameters],
			Body = hasBody ? Span(openBrace, Tokens[index - 1]) : null,
			LastHeaderToken = lastHeaderToken
		};
	}
	
	private bool ParseMembers(ref int index, Token openBrace, Token? block, List<IDeclarationNode> members)
	{
		int? errorsBefore = null;
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				ReportUnclosed(openBrace);
				return true;
			}
			
			if (!SeparateMember(ref index, ref errorsBefore, "member"))
				continue;
			
			RecordSite(index, CompletionSite.TopLevel);
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref i, open, isActive) =>
					    ParseMembers(ref i, open, block, isActive ? members : [])))
					return false;
				
				continue;
			}
			
			if (MatchPubBlock(ref index, _memberContextualKeywords, out var pubKeyword, out var pubBrace))
			{
				if (!allowsPublicMembers)
					Report(pubKeyword, "Cannot declare 'pub' members");
				else if (block is { } outerBlock)
					Report(pubKeyword, $"Cannot use 'pub' in '{outerBlock.Text}' blocks");
				
				if (!ParseMembers(ref index, pubBrace, allowsPublicMembers ? block ?? pubKeyword : block, members))
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
			
			var start = index;
			var window = OpenErrorWindow(index);
			int? lastCut = null;
			var member = ParseWithStrayRecovery(ref index, from =>
			{
				var attempt = ParseMemberWithCut(from, block, out var found);
				lastCut = found;
				return attempt;
			}, static () => { }, out var deleted, separates: SeparatesMembers);
			
			var cut = lastCut is { } found && deleted >= 0 && found >= deleted ? found + 1 : lastCut;
			RecordSyntaxErrors(member, window, index);
			if (member is not null)
			{
				members.Add(member);
				continue;
			}
			
			if (cut is not null)
			{
				droppedMembers++;
				index = cut.Value;
			}
			else if (ResyncMember(ref index, start))
				return false;
		}
		
		return true;
	}
	
	private bool SeparatesMembers(int index) => Peek(index, TokenType.OpComma) &&
	                                            (Peek(index + 1, TokenType.OpCloseBrace) ||
	                                             Peek(index + 1, TokenType.Identifier) &&
	                                             Peek(index + 2, TokenType.OpColon));
	
	private bool SeparatesCases(int index) => Peek(index, TokenType.OpComma) &&
	                                          (Peek(index + 1, TokenType.OpCloseBrace) ||
	                                           Peek(index + 1, TokenType.Identifier));
	
	private (IDeclarationNode? Member, int End) ParseMemberWithCut(int start, Token? block, out int? cut)
	{
		var index = start;
		var state = SaveState(index);
		var member = TryParseMember(ref index, block);
		cut = FindDeclarationCut(start, index, state, member is not null, true);
		if (cut is null)
			return (member, index);
		
		RestoreState(state, index);
		index = start;
		var tokens = Bound(cut.Value);
		try
		{
			member = TryParseMember(ref index, block);
		}
		finally
		{
			Unbind(tokens);
		}
		
		return (member, index);
	}
	
	private IDeclarationNode? TryParseMember(ref int index, Token? block)
	{
		try
		{
			return ParseMember(ref index, block);
		}
		catch (ParseException e) when (!e.IsNesting)
		{
			AddParseError(e);
			return null;
		}
	}
	
	private EnumNode? ParseEnum(ref int index, Token identifier, DeclarationModifiers modifiers, bool isExternal,
		bool isRef, bool isOwn, List<TypeParameterNode> typeParameters)
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
		
		if (ParseTraitList(ref index) is not { } traits)
			return null;
		
		if (MissesBody(index))
		{
			ReportMissing(index, "'{'");
			if (!IsOnSameLine(index))
				return null;
			
			SkipToLineEnd(ref index);
		}
		
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			return new(identifier, modifiers.Tokens, isExternal, isRef, tagType, [], [])
			{
				Extent = Span(identifier, Tokens[index - 1]),
				Visibility = modifiers.Visibility,
				TypeParameters = [..typeParameters],
				MatchedType = matchedType,
				Traits = [..traits],
				ConstantTraits = ConstantsOf(traits),
				IsOwn = isOwn
			};
		
		var cases = new List<EnumCaseNode>();
		var members = new List<IDeclarationNode>();
		if (!ParseEnumBody(ref index, openBrace, null, matchedType, cases, members))
			return null;
		
		return new(identifier, modifiers.Tokens, isExternal, isRef, tagType, cases, members)
		{
			Extent = Span(identifier, Tokens[index - 1]),
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			MatchedType = matchedType,
			Traits = [..traits],
			ConstantTraits = ConstantsOf(traits),
			IsOwn = isOwn
		};
	}
	
	private bool ParseEnumBody(ref int index, Token openBrace, Token? block, ITypeNode? matchedType,
		List<EnumCaseNode> cases, List<IDeclarationNode> members)
	{
		int? errorsBefore = null;
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				ReportUnclosed(openBrace);
				return true;
			}
			
			if (!SeparateMember(ref index, ref errorsBefore, "member"))
				continue;
			
			RecordSite(index, CompletionSite.TopLevel);
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
			
			var window = OpenErrorWindow(index);
			if (MatchOperatorName(ref index, out var symbol))
			{
				var operatorMember = ParseOperatorMember(ref index, symbol, block);
				RecordSyntaxErrors(operatorMember, window, index);
				if (operatorMember is not null)
					members.Add(operatorMember);
				else if (ResyncMember(ref index, window.Start))
					return false;
				
				continue;
			}
			
			if (Match(ref index, out var lifecycleKeyword, _lifecycleKeywords))
			{
				var isConversion = lifecycleKeyword.Type == TokenType.KeywordNew && StartsConversion(index);
				if (!isConversion)
					Report(lifecycleKeyword, $"Cannot declare {DescribeLifecycle(lifecycleKeyword)} in enums");
				
				var lifecycleMember = ParseLifecycleMember(ref index, lifecycleKeyword, block);
				RecordSyntaxErrors(isConversion ? lifecycleMember : null, window, index);
				if (lifecycleMember is null && ResyncMember(ref index, window.Start))
					return false;
				
				if (isConversion && lifecycleMember is not null)
					members.Add(lifecycleMember);
				
				continue;
			}
			
			if (StartsEnumMember(index))
			{
				var member = ParseEnumMember(ref index, block);
				RecordSyntaxErrors(member, window, index);
				if (member is not null)
					members.Add(member);
				else if (ResyncMember(ref index, window.Start))
					return false;
				
				continue;
			}
			
			var caseStart = index;
			if (Match(ref index, out var caseVisibility, _memberContextualKeywords, _visibilityKeywords) &&
			    Peek(index, TokenType.Identifier) && IsOnSameLine(index))
				Report(caseVisibility, $"Cannot declare '{caseVisibility.Text}' cases");
			else
				index = caseStart;
			
			var parsedCase = ParseWithStrayRecovery(ref index, from =>
			{
				var end = from;
				var attempt = ParseEnumCase(ref end, matchedType);
				return (attempt, end);
			}, static () => { }, out _, separates: SeparatesCases);
			
			if (parsedCase is not { } enumCase)
			{
				if (ResyncMember(ref index, caseStart))
					return false;
				
				continue;
			}
			
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
		
		if (!Peek(index, TokenType.OpEqual))
			SkipStray(ref index, TokenType.OpOpenParen);
		
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
		if (MatchOnLine(ref index, TokenType.OpEqual))
		{
			if (Match(ref index, out var keyword, TokenType.KeywordElse))
				elseKeyword = keyword;
			else if (StartsCaseValue(index))
				values.AddRange(ParseCaseValues(ref index, matchedType is not null));
			else
				ReportExpected(index - 1, "an expression");
		}
		
		return new(name, payload, values, elseKeyword);
	}
	
	private bool StartsCaseValue(int index) => !AtEnd(index) && !Peek(index, TokenType.OpCloseBrace) &&
	                                           !Peek(index, TokenType.OpComma) &&
	                                           (IsOnSameLine(index) || !StartsEnumItem(index, true));
	
	private List<IExpressionNode> ParseCaseValues(ref int index, bool isMatched)
	{
		var values = new List<IExpressionNode> { ParseExpression(ref index) };
		while (Peek(index, TokenType.OpComma) && !StartsEnumItem(index + 1, isMatched) &&
		       Match(ref index, TokenType.OpComma))
			values.Add(ParseExpression(ref index));
		
		return values;
	}
	
	private bool StartsEnumItem(int index, bool isMatched)
	{
		if (AtEnd(index) || Peek(index + 1, TokenType.OpColon) || Peek(index, TokenType.KeywordWhen))
			return true;
		
		if (!Peek(index, TokenType.Identifier))
			return false;
		
		var next = index + 1;
		if (Peek(next, TokenType.OpOpenParen) && IsOnSameLine(next))
		{
			if (FindClosingParen(next) is not { } close)
				return false;
			
			next = close + 1;
		}
		
		if (Peek(next, TokenType.OpEqual) || Peek(next, TokenType.OpOpenBrace) || Peek(next, TokenType.OpOpenBracket))
			return true;
		
		return !isMatched &&
		       (AtEnd(next) || !IsOnSameLine(next) || Peek(next, TokenType.OpComma) ||
		        Peek(next, TokenType.OpCloseBrace));
	}
	
	private int? FindClosingParen(int index)
	{
		var depth = 0;
		for (var i = index; !AtEnd(i); i++)
		{
			if (Peek(i, TokenType.OpOpenParen))
				depth++;
			else if (Peek(i, TokenType.OpCloseParen) && --depth == 0)
				return i;
		}
		
		return null;
	}
	
	private bool ParsePayload(ref int index, Token openParen, List<FieldNode> payload)
	{
		do
		{
			if (EndsPayloadLine(index))
			{
				ReportExpected(index - 1, "')'", openParen);
				return true;
			}
			
			if (!Match(ref index, out var fieldName, TokenType.Identifier))
			{
				ReportExpected(index, "a payload name");
				return false;
			}
			
			var errors = Diagnostics.ErrorCount;
			ITypeNode type;
			if (!Match(ref index, TokenType.OpColon))
			{
				ReportMissing(index, "':' and a type");
				type = MissingType(index);
			}
			else
			{
				if (Tokens[index].Text == "req" && StartsMemberType(index + 1))
					Report(Tokens[index++], "Cannot declare required fields in enums");
				
				type = ParseRecoveredType(ref index);
			}
			
			payload.Add(new FieldNode(fieldName, type, []));
			SkipBrokenParameter(ref index, errors);
		} while (MatchSeparator(ref index, TokenType.OpCloseParen));
		
		if (Match(ref index, TokenType.OpCloseParen))
			return true;
		
		ReportExpected(StartsLine(index) ? index - 1 : index, "',' or ')'", openParen);
		return EndsParameters(index) || StartsLine(index) && StartsEnumItem(index, false);
	}
	
	private bool EndsPayloadLine(int index) =>
		StartsLine(index) && (AtEnd(index) || Tokens[index].Type != TokenType.Identifier ||
		                      !Peek(index + 1, TokenType.OpColon) || StartsMemberDeclaration(index));
	
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