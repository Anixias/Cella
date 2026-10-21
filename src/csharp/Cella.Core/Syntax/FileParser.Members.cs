using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private IDeclarationNode? ParseMember(ref int index, Token? block)
	{
		if (Match(ref index, out var lifecycleKeyword, _lifecycleKeywords))
			return ParseLifecycleMember(ref index, lifecycleKeyword, block);
		
		if (MatchOperatorName(ref index, out var symbol))
			return ParseOperatorMember(ref index, symbol, block);
		
		if (IsReservedWord(index) && Peek(index + 1, TokenType.OpColon))
		{
			Report(Tokens[index], DescribeReservedWord(Tokens[index]));
			return null;
		}
		
		if (!Match(ref index, out var identifier, TokenType.Identifier))
		{
			ReportUnexpected(Tokens[index]);
			return null;
		}
		
		RejectDiscardName(identifier);
		if (identifier.Text == "ext" && Peek(index, TokenType.OpOpenBrace))
		{
			Report(identifier, "Cannot nest 'ext' blocks");
			return null;
		}
		
		if (identifier.Text == "use" && !Peek(index, TokenType.OpColon) && !Peek(index, TokenType.OpOpenBracket))
		{
			Report(identifier, "Cannot nest 'use' declarations");
			return null;
		}
		
		if (ParseTypeParameters(ref index) is not { } typeParameters)
			return null;
		
		RejectReservedNames(typeParameters);
		if (!Match(ref index, TokenType.OpColon))
		{
			Report(Tokens[index], $"Expected ':' after '{identifier.Text}'");
			return null;
		}
		
		var modifiers = ParseMemberModifiers(ref index, block);
		if (_nestedKinds.Contains(Tokens[index].Text))
		{
			Report(Tokens[index], $"Cannot nest '{Tokens[index].Text}' declarations");
			return null;
		}
		
		RejectBuiltInName(identifier);
		
		if (Tokens[index].Text == "ext" && Peek(index + 1, TokenType.KeywordFun) &&
		    !Peek(index + 2, TokenType.OpOpenBracket))
		{
			Report(Tokens[index], "Cannot declare 'ext' functions in types");
			return null;
		}
		
		if (FindMisplacedFieldModifier(index) is var (location, message))
		{
			Report(location, message);
			return null;
		}
		
		if (IsFunctionMember(index))
		{
			RejectWriteRestriction(modifiers, "functions");
			Match(ref index, TokenType.KeywordFun);
			return ParseDeclaration(ref index, identifier, "function",
				(ref i) => ParseFunction(ref i, identifier, modifiers, false, typeParameters));
		}
		
		RejectMemberTypeParameters(typeParameters, index);
		if (Match(ref index, out var bindingKeyword, _bindingKeywords))
			return ParseStaticField(ref index, identifier, modifiers, bindingKeyword);
		
		if (StartsProperty(index))
			return ParseDeclaration(ref index, identifier, "property",
				(ref i) => ParseProperty(ref i, identifier, modifiers));
		
		// Fields
		return ParseField(ref index, identifier, modifiers);
	}
	
	private void RejectMemberTypeParameters(List<TypeParameterNode> typeParameters, int index)
	{
		if (typeParameters is not [var first, ..])
			return;
		
		var kind = StartsProperty(index) ? "properties" : "fields";
		Report(first.SourceLocation, $"Cannot declare type parameters on {kind}");
	}
	
	private bool MatchOperatorName(ref int index, out Token symbol)
	{
		if (Peek(index + 1, TokenType.OpColon) && Match(ref index, out symbol, TokenType.KeywordPanic))
		{
			Report(symbol, "Cannot declare 'panic' operators in types");
			return true;
		}
		
		if (Peek(index + 1, TokenType.OpColon) && Match(ref index, out symbol, _undeclarableOperators))
		{
			Report(symbol, $"Cannot declare '{symbol.Text}' operators");
			return true;
		}
		
		if ((Peek(index + 1, TokenType.OpColon) || Peek(index + 1, TokenType.OpOpenBracket)) &&
		    Match(ref index, out symbol, _conversionKeywords, TokenType.KeywordAs))
			return true;
		
		if (Peek(index, TokenType.OpOpenBracket) && Peek(index + 1, TokenType.OpCloseBracket))
		{
			var (source, range) = Tokens[index].SourceLocation;
			range = range.Join(Tokens[index + 1].SourceLocation.Range);
			symbol = new Token(TokenType.OpOpenBracket, new SourceLocation(source, range), "[]");
			index += 2;
			return true;
		}
		
		return Match(ref index, out symbol, _operatorNames);
	}
	
	private FunctionNode? ParseOperatorMember(ref int index, Token symbol, Token? block) =>
		ParseTypeParameters(ref index) is { } typeParameters
			? ParseDeclaration(ref index, symbol, "operator",
				(ref i) => ParseOperator(ref i, symbol, block, typeParameters))
			: null;
	
	private FunctionNode? ParseOperator(ref int index, Token symbol, Token? block,
		List<TypeParameterNode> typeParameters)
	{
		if (!Match(ref index, TokenType.OpColon))
		{
			Report(Tokens[index], $"Expected ':' after '{symbol.Text}'");
			return null;
		}
		
		var modifiers = symbol.Type == TokenType.KeywordPanic
			? ParseDeclarationModifiers(ref index, block)
			: ParseMemberModifiers(ref index, block);
		
		RejectWriteRestriction(modifiers, "operators");
		if (Match(ref index, _memberContextualKeywords, TokenType.KeywordOp))
			return ParseFunction(ref index, symbol, modifiers, false, typeParameters);
		
		ReportExpected(index, "'op'");
		return null;
	}
	
	private bool IsFunctionMember(int index) =>
		Match(ref index, TokenType.KeywordFun) && !Peek(index, TokenType.OpOpenBracket);
	
	private bool StartsProperty(int index)
	{
		if (Match(ref index, _memberContextualKeywords, TokenType.KeywordProp))
			return !IsOnSameLine(index) || Tokens[index].Type is TokenType.Identifier or TokenType.KeywordFun
				or TokenType.KeywordRef or TokenType.KeywordImm or TokenType.KeywordMut or TokenType.OpOpenBrace;
		
		return Match(ref index, _memberContextualKeywords, _accessorKeywords) && IsOnSameLine(index) &&
		       Tokens[index].Type is TokenType.OpOpenParen or TokenType.OpArrow or TokenType.OpFatArrow
			       or TokenType.OpOpenBrace;
	}
	
	private PropertyNode? ParseProperty(ref int index, Token identifier, DeclarationModifiers modifiers)
	{
		RejectWriteRestriction(modifiers, "properties");
		if (!Match(ref index, out var keyword, _memberContextualKeywords, TokenType.KeywordProp))
			return ParseAccessor(ref index, null, [], null) is { } accessor
				? new(identifier, modifiers.Tokens, null, null, [accessor]) { Visibility = modifiers.Visibility }
				: null;
		
		var type = ParseValueType(ref index);
		var accessors = new List<FunctionNode>();
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace))
		{
			int? errorsBefore = null;
			while (!Match(ref index, TokenType.OpCloseBrace))
			{
				if (AtEnd(index))
				{
					ReportUnclosed(openBrace);
					return null;
				}
				
				if (!SeparateMember(ref index, ref errorsBefore, "accessor"))
					continue;
				
				if (ParsePropertyAccessor(ref index, type, modifiers.Visibility) is not { } accessor)
					return null;
				
				accessors.Add(accessor);
			}
		}
		
		var restricted = accessors.Where(static accessor => accessor.Visibility is not null).ToList();
		if (restricted.Count > 1)
		{
			foreach (var accessor in restricted)
				Report(accessor.Visibility!.Value, "Cannot restrict both accessors");
		}
		
		return new(identifier, modifiers.Tokens, keyword, type, accessors) { Visibility = modifiers.Visibility };
	}
	
	private FunctionNode? ParsePropertyAccessor(ref int index, ITypeNode type, Token? propertyVisibility)
	{
		var keywords = new List<Token>();
		while (Match(ref index, out var keyword, _memberContextualKeywords, _visibilityKeywords))
			keywords.Add(keyword);
		
		var visibility = CheckVisibility(keywords, null, true);
		if (keywords is [{ Type: not TokenType.KeywordPub } narrowed] && propertyVisibility is { } outer &&
		    !IsNarrower(narrowed, outer))
			Report(narrowed, $"Cannot use '{narrowed.Text}' in '{outer.Text}' properties");
		
		return ParseAccessor(ref index, type, keywords, visibility);
	}
	
	private FunctionNode? ParseAccessor(ref int index, ITypeNode? propertyType, List<Token> modifiers,
		Token? visibility)
	{
		if (!Match(ref index, out var keyword, _memberContextualKeywords, _accessorKeywords))
		{
			ReportExpected(index, "'get' or 'set'");
			return null;
		}
		
		if (ParseAccessorParameters(ref index, propertyType) is not var (receiver, parameters))
			return null;
		
		var isGetter = keyword.Type == TokenType.KeywordGet;
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseRecoveredType(ref index) : null;
		if (isGetter && parameters.Count > 0)
			Report(parameters[0].SourceLocation, "Getters cannot take parameters");
		else if (!isGetter && parameters.Count != 1)
			Report(parameters.Count > 1 ? parameters[1].SourceLocation : keyword.SourceLocation,
				"Setters must take one parameter");
		
		if (!isGetter && returnType is not null)
			Report(returnType.SourceLocation, "Setters cannot return values");
		else if (isGetter && returnType is null && propertyType is null)
			ReportExpected(index, "'->' and a type");
		
		var resultType = isGetter ? returnType ?? propertyType : null;
		RejectWhen(ref index, isGetter ? "getters" : "setters");
		IStatementNode? body;
		Token? arrow = null;
		if (MatchOnLine(ref index, TokenType.OpFatArrow))
		{
			arrow = Tokens[index - 1];
			body = ParseExpressionBody(ref index, resultType is null);
		}
		else if (Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			body = ParseBlockStatement(ref index, openBrace);
		else
		{
			if (!allowsMissingBodies)
			{
				ReportExpected(IsOnSameLine(index) ? index : index - 1, "'=>' or '{'");
				if (!EndsDeclaration(index))
					return null;
			}
			
			return new(keyword, modifiers, receiver, parameters, resultType, null, false)
			{
				Extent = Span(keyword, Tokens[index - 1]),
				Visibility = visibility
			};
		}
		
		return body is null
			? null
			: new(keyword, modifiers, receiver, parameters, resultType, body, false)
			{
				Extent = Span(keyword, Tokens[index - 1]),
				Visibility = visibility,
				BodyArrow = arrow
			};
	}
	
	private (ReceiverNode? Receiver, List<ParameterNode> Parameters)? ParseAccessorParameters(ref int index,
		ITypeNode? propertyType)
	{
		var parameters = new List<ParameterNode>();
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
			return (null, parameters);
		
		var receiver = ParseReceiver(ref index);
		if (Match(ref index, TokenType.OpCloseParen))
			return (receiver, parameters);
		
		if (receiver is not null && !Match(ref index, TokenType.OpComma))
		{
			ReportExpected(index, "',' or ')'", openParen);
			return null;
		}
		
		if (receiver is not null && RejectTrailingComma(index, Tokens[index - 1], TokenType.OpCloseParen) &&
		    Match(ref index, TokenType.OpCloseParen))
			return (receiver, parameters);
		
		do
		{
			Token? mode = Match(ref index, out var keyword, TokenSets.ParameterModes) ? keyword : null;
			if (!Match(ref index, out var name, TokenType.Identifier))
			{
				if (IsReservedWord(index))
					Report(Tokens[index], DescribeReservedWord(Tokens[index]));
				else
					ReportExpected(index, "a parameter name");
				
				return null;
			}
			
			var errors = Diagnostics.ErrorCount;
			if (Match(ref index, TokenType.OpColon))
				parameters.Add(new(mode, name, ParseRecoveredType(ref index), null));
			else if (propertyType is not null)
				parameters.Add(new(mode, name, propertyType, null));
			else
			{
				ReportMissing(index, "':' and a type");
				parameters.Add(new(mode, name, MissingType(index), null));
			}
			
			SkipBrokenParameter(ref index, errors);
		} while (MatchSeparator(ref index, TokenType.OpCloseParen));
		
		if (Match(ref index, TokenType.OpCloseParen))
			return (receiver, parameters);
		
		ReportExpected(index, "',' or ')'", openParen);
		return null;
	}
	
	private (SourceLocation Location, string Message)? FindMisplacedFieldModifier(int index)
	{
		if (AtEnd(index + 1) || !IsOnSameLine(index + 1))
			return null;
		
		var token = Tokens[index];
		var next = Tokens[index + 1];
		if (token.Text == "set" && StartsMemberType(index + 1))
			return (token.SourceLocation, "Expected 'pvt' or 'mod' before 'set'");
		
		if (token.Text != "req")
			return null;
		
		var following = index + 1;
		if (next.Text is "pvt" or "mod" or "pub")
			return (next.SourceLocation, $"Cannot use '{next.Text}' after 'req'");
		
		if (next.Text == "req")
			return (next.SourceLocation, "Cannot repeat 'req'");
		
		if (_bindingKeywords.Contains(next.Type))
			return (token.SourceLocation, "Cannot declare required static fields");
		
		if (IsFunctionMember(following))
			return (token.SourceLocation, "Cannot declare required functions");
		
		if (LookAhead(() => Match(ref following, _memberContextualKeywords, TokenType.KeywordOp)))
			return (token.SourceLocation, "Cannot declare required operators");
		
		return StartsProperty(index + 1) ? (token.SourceLocation, "Cannot declare required properties") : null;
	}
	
	private List<Token> ParseFieldModifiers(ref int index)
	{
		var modifiers = new List<Token>();
		if (StartsType(index + 1) && Tokens[index + 1].Line == Tokens[index].Line &&
		    Match(ref index, out var reqToken, _memberContextualKeywords, TokenType.KeywordReq))
			modifiers.Add(reqToken);
		
		return modifiers;
	}
	
	private IDeclarationNode? ParseLifecycleMember(ref int index, Token keyword, Token? block)
	{
		if (!RejectTypeParameters(ref index, DescribeLifecycle(keyword)))
			return null;
		
		return keyword.Type == TokenType.KeywordNew
			? ParseConstructor(ref index, keyword, block)
			: ParseDestructor(ref index, keyword, block);
	}
	
	private static string DescribeLifecycle(Token keyword) =>
		keyword.Type == TokenType.KeywordNew ? "constructors" : "destructors";
	
	private IDeclarationNode? ParseConstructor(ref int index, Token newKeyword, Token? block)
	{
		// Caller is expected to resync in case of errors
		
		var isConversion = StartsConversion(index);
		if (!Match(ref index, TokenType.OpColon))
		{
			Report(Tokens[index], $"Expected ':' after '{newKeyword.Text}'");
			return null;
		}
		
		var modifiers = ParseMemberModifiers(ref index, block);
		RejectWriteRestriction(modifiers, isConversion ? "operators" : "constructors");
		if (!Match(ref index, _memberContextualKeywords, TokenType.KeywordOp))
		{
			ReportExpected(index, "'op'");
			return null;
		}
		
		if (isConversion)
			return ParseFunction(ref index, newKeyword, modifiers, false, []);
		
		var parametersStart = index;
		var errorsBefore = Diagnostics.ErrorCount;
		if (ParseParameters(ref index) is not { } parameters)
			return null;
		
		var parameterErrors = Diagnostics.ErrorCount - errorsBefore;
		ReportEmptyParentheses(parametersStart, index);
		RejectReturnType(ref index, "Constructors");
		RejectWhen(ref index, "constructors");
		if ((parameters.Count == 0 || Peek(index + 1, TokenType.KeywordDefault)) &&
		    MatchOnLine(ref index, TokenType.OpEqual))
			return ParseDefaultConstructor(ref index, newKeyword, modifiers, parameters);
		
		if (!Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
		{
			if (!allowsMissingBodies)
			{
				if (parameterErrors == 0)
					ReportExpected(index, "'{'");
				
				if (!EndsDeclaration(index))
					return null;
			}
			
			return new ConstructorNode(newKeyword, modifiers.Tokens, parameters, null, newKeyword.SourceLocation)
			{
				Visibility = modifiers.Visibility
			};
		}
		
		if (ParseBlockStatement(ref index, openBraceToken) is not { } body)
			return null;
		
		var (source, range) = newKeyword.SourceLocation;
		range = range.Join(body.SourceLocation.Range);
		
		return new ConstructorNode(newKeyword, modifiers.Tokens, parameters, body, new(source, range))
		{
			Visibility = modifiers.Visibility
		};
	}
	
	private ConstructorNode? ParseDefaultConstructor(ref int index, Token newKeyword, DeclarationModifiers modifiers,
		List<ParameterNode> parameters)
	{
		if (!Match(ref index, out var defaultKeyword, TokenType.KeywordDefault))
		{
			ReportExpected(index, "'default'");
			return null;
		}
		
		if (parameters.Count > 0)
		{
			Report(defaultKeyword, "Cannot use 'default' on constructors with parameters");
			return null;
		}
		
		var (source, range) = newKeyword.SourceLocation;
		var location = new SourceLocation(source, range.Join(defaultKeyword.SourceLocation.Range));
		return new ConstructorNode(newKeyword, modifiers.Tokens, parameters, new BlockStatementNode(location, []),
			location)
		{
			Visibility = modifiers.Visibility,
			Default = defaultKeyword
		};
	}
	
	private void RejectExternalBody(ref int index)
	{
		if (!IsOnSameLine(index) || !Peek(index, TokenType.OpFatArrow) && !Peek(index, TokenType.OpOpenBrace))
			return;
		
		Report(Tokens[index], "Cannot declare bodies for 'ext' functions");
		if (Peek(index, TokenType.OpOpenBrace))
		{
			var depth = 0;
			do
			{
				if (Peek(index, TokenType.OpOpenBrace))
					depth++;
				else if (Peek(index, TokenType.OpCloseBrace))
					depth--;
				
				index++;
			} while (depth > 0 && !AtEnd(index));
			
			return;
		}
		
		do
			index++;
		while (IsOnSameLine(index));
	}
	
	private void RejectReturnType(ref int index, string kind)
	{
		if (!IsOnSameLine(index) || !Match(ref index, out var arrow, TokenType.OpArrow))
			return;
		
		Report(arrow, $"{kind} cannot return values");
		ParseRecoveredType(ref index);
	}
	
	private void RejectWhen(ref int index, string kind)
	{
		if (!IsOnSameLine(index) || !Match(ref index, out var whenKeyword, TokenType.KeywordWhen))
			return;
		
		Report(whenKeyword, $"Cannot use 'when' on {kind}");
		ParseCondition(ref index);
	}
	
	private bool StartsConversion(int index)
	{
		if (!Match(ref index, TokenType.OpColon))
			return false;
		
		while (Match(ref index, _memberContextualKeywords, _visibilityKeywords) ||
		       Match(ref index, _memberContextualKeywords, TokenType.KeywordSet))
		{
		}
		
		if (!Match(ref index, _memberContextualKeywords, TokenType.KeywordOp) ||
		    !Match(ref index, TokenType.OpOpenParen))
			return false;
		
		Match(ref index, TokenSets.ParameterModes);
		return Peek(index, TokenType.KeywordSelf);
	}
	
	private DestructorNode? ParseDestructor(ref int index, Token dropKeyword, Token? block)
	{
		if (!Match(ref index, TokenType.OpColon))
		{
			Report(Tokens[index], $"Expected ':' after '{dropKeyword.Text}'");
			return null;
		}
		
		var modifiers = ParseMemberModifiers(ref index, block);
		RejectWriteRestriction(modifiers, "destructors");
		if (modifiers.Visibility is { Type: not TokenType.KeywordPub } visibility)
			Report(modifiers.Tokens.Contains(visibility) ? visibility : dropKeyword,
				$"Cannot declare '{visibility.Text}' destructors");
		
		if (!Match(ref index, _memberContextualKeywords, TokenType.KeywordOp))
		{
			ReportExpected(index, "'op'");
			return null;
		}
		
		var parametersStart = index;
		if (ParseParameters(ref index, false, true, out _, out var receiver) is not { } parameters)
			return null;
		
		ReportEmptyParentheses(parametersStart, index);
		if (receiver is not null)
			Report(receiver.Self, "Destructors cannot take parameters");
		else if (parameters.Count > 0)
			Report(parameters[0].SourceLocation, "Destructors cannot take parameters");
		
		RejectReturnType(ref index, "Destructors");
		RejectWhen(ref index, "destructors");
		if (!Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
		{
			if (!allowsMissingBodies)
			{
				ReportExpected(index, "'{'");
				if (!EndsDeclaration(index))
					return null;
			}
			
			return new(dropKeyword, null, dropKeyword.SourceLocation);
		}
		
		if (ParseBlockStatement(ref index, openBraceToken) is not { } body)
			return null;
		
		var (source, range) = dropKeyword.SourceLocation;
		range = range.Join(body.SourceLocation.Range);
		
		return new(dropKeyword, body, new(source, range));
	}
	
	private FieldNode? ParseField(ref int index, Token identifier, DeclarationModifiers modifiers)
	{
		var tokens = modifiers.Tokens.Concat(ParseFieldModifiers(ref index));
		var type = ParseValueType(ref index);
		if (IsOnSameLine(index) && Peek(index, TokenType.OpEqual))
		{
			Report(Tokens[index], "Cannot give fields initial values");
			return null;
		}
		
		return new(identifier, type, tokens)
		{
			Visibility = modifiers.Visibility,
			WriteVisibility = modifiers.WriteVisibility
		};
	}
}