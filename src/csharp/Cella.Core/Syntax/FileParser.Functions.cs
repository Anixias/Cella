using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private FunctionNode? ParseFunction(ref int index, Token identifier, DeclarationModifiers modifiers,
		bool isExternal, List<TypeParameterNode> typeParameters, Token? externalName = null)
	{
		// When this is called, the identifier and fun keyword are already consumed
		// Caller is expected to resync in case of errors
		
		var errorsBefore = Diagnostics.ErrorCount;
		if (ParseFunctionSignature(ref index, false, true) is not { } signature)
			return null;
		
		var (receiver, parameters, returnType, _) = signature;
		WhenClause? when = null;
		if (IsOnSameLine(index) && Match(ref index, out var whenKeyword, TokenType.KeywordWhen) &&
		    ParseCondition(ref index) is { } condition)
			when = new(whenKeyword, condition);
		
		var headerErrors = Diagnostics.ErrorCount - errorsBefore;
		
		if (MatchOnLine(ref index, TokenType.OpFatArrow))
		{
			var arrow = Tokens[index - 1];
			if (ParseExpressionBody(ref index, returnType is null) is not { } statement)
				return null;
			
			return new(identifier, modifiers.Tokens, receiver, parameters, returnType, statement, isExternal)
			{
				Extent = Span(identifier, Tokens[index - 1]),
				BodyArrow = arrow,
				Visibility = modifiers.Visibility,
				TypeParameters = [..typeParameters],
				When = when,
				ExternalName = externalName
			};
		}
		
		if (!Match(ref index, out var openBraceToken, TokenType.OpOpenBrace) &&
		    !SkipToBody(ref index, out openBraceToken, "'=>' or '{'"))
		{
			if (!allowsMissingBodies)
			{
				if (headerErrors == 0)
					ReportExpected(IsOnSameLine(index) ? index : index - 1, "'=>' or '{'");
				
				if (!EndsDeclaration(index))
					return null;
			}
			
			return new(identifier, modifiers.Tokens, receiver, parameters, returnType, null, isExternal)
			{
				Extent = Span(identifier, Tokens[index - 1]),
				Visibility = modifiers.Visibility,
				TypeParameters = [..typeParameters],
				When = when,
				ExternalName = externalName
			};
		}
		
		if (ParseBlockStatement(ref index, openBraceToken) is not { } body)
			return null;
		
		return new(identifier, modifiers.Tokens, receiver, parameters, returnType, body, isExternal)
		{
			Extent = Span(identifier, Tokens[index - 1]),
			Visibility = modifiers.Visibility,
			ExternalName = externalName,
			TypeParameters = [..typeParameters],
			When = when
		};
	}
	
	private bool SkipToBody(ref int index, out Token openBrace, string expected)
	{
		openBrace = default;
		var skipped = index;
		SkipToLineEnd(ref skipped);
		if (skipped == index || !Match(ref skipped, out openBrace, TokenType.OpOpenBrace))
			return false;
		
		ReportExpected(index, expected);
		index = skipped;
		return true;
	}
	
	private bool EndsDeclaration(int index) => !IsOnSameLine(index) || Peek(index, TokenType.OpCloseBrace);
	
	private bool EndsParameters(int index) =>
		AtEnd(index) || Peek(index, TokenType.OpCloseBrace) || StartsDeclarationOnLine(index);
	
	private int SkipExpressionBody(int start)
	{
		var braces = 0;
		var i = start;
		for (; !AtEnd(i); i++)
		{
			if (braces == 0 && (Peek(i, TokenType.OpCloseBrace) || i > start && StartsDeclarationOnLine(i)))
				break;
			
			if (Peek(i, TokenType.OpOpenBrace))
				braces++;
			else if (Peek(i, TokenType.OpCloseBrace))
				braces--;
		}
		
		return i;
	}
	
	private IStatementNode? ParseExpressionBody(ref int index, bool discardsValue)
	{
		if (discardsValue && Match(ref index, out var matchToken, TokenType.KeywordMatch))
			return ParseMatchStatement(ref index, matchToken);
		
		var start = index;
		var errorsBefore = Diagnostics.ErrorCount;
		IStatementNode body;
		try
		{
			var expression = ParseExpression(ref index);
			body = new ExpressionStatementNode(discardsValue
				? expression
				: new ReturnExpressionNode(expression.SourceLocation, expression));
		}
		catch (ParseException e)
		{
			AddParseError(e);
			index = SkipExpressionBody(start);
			body = new BrokenStatementNode(Span(Tokens[start], Tokens[Math.Max(index - 1, start)]), null, false);
		}
		
		ReportLexicalErrors(start, index);
		if (Diagnostics.ErrorCount == errorsBefore)
			return body;
		
		_brokenStatements.Add(body);
		attributedErrors += Diagnostics.ErrorCount - errorsBefore;
		return body;
	}
	
	private GlobalNode? ParseGlobal(ref int index, Token identifier, DeclarationModifiers modifiers, Token keyword,
		bool isExternal = false, Token? externalName = null)
	{
		var type = ParseValueType(ref index);
		IExpressionNode? initializer = null;
		if (MatchOnLine(ref index, TokenType.OpEqual))
		{
			if (StartsValue(index))
				initializer = ParseExpression(ref index);
			else
				ReportExpected(index - 1, "an expression");
		}
		else if (!allowsMissingBodies || keyword.Type != TokenType.KeywordConst)
		{
			Report(type.SourceLocation, "Expected '=' and an initial value");
			if (!EndsDeclaration(index))
				return null;
		}
		
		return new(identifier, keyword, type, initializer)
		{
			Extent = Span(identifier, Tokens[index - 1]),
			Visibility = modifiers.Visibility,
			WriteVisibility = modifiers.WriteVisibility,
			IsExternal = isExternal,
			ExternalName = externalName
		};
	}
	
	private ITypeNode ParseValueType(ref int index)
	{
		if (!IsOnSameLine(index))
		{
			ReportMissing(index, "a type");
			return MissingType(index);
		}
		
		var errors = Diagnostics.ErrorCount;
		var type = ParseRecoveredType(ref index);
		if (Diagnostics.ErrorCount > errors)
			SkipBrokenType(ref index);
		
		return type;
	}
	
	private void SkipBrokenType(ref int index)
	{
		var depth = 0;
		for (; IsOnSameLine(index); index++)
		{
			switch (Tokens[index].Type)
			{
				case TokenType.OpEqual or TokenType.OpComma or TokenType.OpCloseBrace when depth == 0:
					return;
				
				case TokenType.OpOpenParen or TokenType.OpOpenBracket:
					depth++;
					break;
				
				case TokenType.OpCloseParen or TokenType.OpCloseBracket:
					depth = Math.Max(depth - 1, 0);
					break;
			}
		}
	}
	
	private bool StartsValue(int index) => !Peek(index, TokenType.OpCloseBrace) &&
	                                       (IsOnSameLine(index) || !AtEnd(index) && !StartsDeclarationOnLine(index));
	
	private bool EndsListItem(int index, TokenType close) =>
		AtEnd(index) || Peek(index, close) || Peek(index, TokenType.OpComma);
	
	private static bool IsExternalIdentifier(string name) =>
		!char.IsAsciiDigit(name[0]) && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');
	
	private Token? ParseExternalName(ref int index)
	{
		if (!Match(ref index, TokenType.OpOpenParen))
		{
			ReportExpected(index, "'('");
			return null;
		}
		
		if (!Match(ref index, out var name, TokenType.StringLiteral) || name.Suffix is not null)
		{
			ReportExpected(index, "a name");
			return null;
		}
		
		if (!Match(ref index, TokenType.OpCloseParen))
		{
			ReportExpected(index, "')'");
			return null;
		}
		
		if (name.Text.Length == 0)
			Report(name, "Cannot use empty 'ext' names");
		else if (!IsExternalIdentifier(name.Text))
			Report(name, $"Cannot use '{name.Text}' as an 'ext' name");
		
		return name;
	}
	
	private GlobalNode? ParseStaticField(ref int index, Token identifier, DeclarationModifiers modifiers, Token keyword)
	{
		if (keyword.Type is TokenType.KeywordVal or TokenType.KeywordConst)
			RejectWriteRestriction(modifiers, $"'{keyword.Text}' fields");
		
		return ParseDeclaration(ref index, identifier, "field",
			(ref i) => ParseGlobal(ref i, identifier, modifiers, keyword));
	}
	
	private IDeclarationNode? ParseExternalDeclaration(ref int index, Token? origin, Token? block)
	{
		Token identifier;
		if (StartsContextualName(index))
			identifier = Tokens[index++];
		else if (!Match(ref index, out identifier, _topLevelContextualKeywords, TokenType.Identifier))
		{
			if (IsReservedWord(index))
				Report(Tokens[index], DescribeReservedWord(Tokens[index]));
			else
				ReportExpected(index, "a name");
			
			return null;
		}
		
		RejectReservedName(identifier);
		if (!RejectTypeParameters(ref index, "ext functions"))
			return null;
		
		if (!Match(ref index, TokenType.OpColon))
		{
			ReportExpected(index, "':'");
			return null;
		}
		
		var modifiers = ParseDeclarationModifiers(ref index, block);
		Token? externalName = null;
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordExt))
		{
			externalName = ParseExternalName(ref index);
			if (externalName is null)
				return null;
		}
		
		if (Match(ref index, out var keyword, _externalBindingKeywords))
		{
			var type = ParseValueType(ref index);
			if (Match(ref index, out var equals, TokenType.OpEqual))
			{
				Report(equals, "Cannot initialize values in 'ext' blocks");
				if (StartsValue(index))
					ParseExpression(ref index);
			}
			
			return new GlobalNode(identifier, keyword, type, null)
			{
				Visibility = modifiers.Visibility,
				WriteVisibility = modifiers.WriteVisibility,
				IsExternal = true,
				ExternalName = externalName,
				Origin = origin?.Text,
				OriginLocation = origin?.SourceLocation
			};
		}
		
		if (!Match(ref index, TokenType.KeywordFun))
		{
			ReportExpected(index, "'fun', 'val' or 'var'");
			return null;
		}
		
		if (ParseFunctionSignature(ref index, true, false) is not { } signature)
			return null;
		
		RejectWhen(ref index, "'ext' functions");
		RejectExternalBody(ref index);
		var (_, parameters, returnType, isVariadic) = signature;
		return new ExternalFunctionNode(identifier, modifiers.Tokens, parameters, returnType, isVariadic, origin)
		{
			Visibility = modifiers.Visibility,
			ExternalName = externalName
		};
	}
	
	private List<ParameterNode>? ParseParameters(ref int index) =>
		ParseParameters(ref index, false, false, out _, out _);
	
	private List<ParameterNode>? ParseParameters(ref int index, bool allowVariadic, bool allowReceiver,
		out bool isVariadic, out ReceiverNode? receiver)
	{
		isVariadic = false;
		receiver = null;
		var parameters = new List<ParameterNode>();
		
		// Parentheses are optional for function declarations
		if (Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			receiver = allowReceiver ? ParseReceiver(ref index) : null;
			if (Match(ref index, TokenType.OpCloseParen))
				return parameters;
			
			if (receiver is not null && !Match(ref index, TokenType.OpComma))
			{
				if (!IsOnSameLine(index))
				{
					ReportExpected(index - 1, "',' or ')'", openParen);
					return parameters;
				}
				
				if (Peek(index, TokenType.OpColon))
					Report(Tokens[index], "Cannot declare a type for 'self'");
				else
					ReportExpected(index, "',' or ')'", openParen);
				
				return null;
			}
			
			if (receiver is not null && RejectTrailingComma(index, Tokens[index - 1], TokenType.OpCloseParen) &&
			    Match(ref index, TokenType.OpCloseParen))
				return parameters;
			
			var errors = Diagnostics.ErrorCount;
			if (ParseParameterList(ref index, allowVariadic, out isVariadic) is not { } parameterList)
				return null;
			
			parameters.AddRange(parameterList);
			if (!Match(ref index, TokenType.OpCloseParen))
			{
				var ends = EndsParameterLine(index);
				if (!ends || Diagnostics.ErrorCount == errors)
					ReportExpected(ends || StartsMemberDeclaration(index) ? index - 1 : index,
						parameters.Count > 0 ? "',' or ')'" : "')'", openParen);
				
				if (!ends && !EndsParameters(index))
					return null;
			}
		}
		
		return parameters;
	}
	
	private void ReportEmptyParentheses(int start, int end)
	{
		if (end != start + 2 || Tokens[start].Type != TokenType.OpOpenParen ||
		    Tokens[start + 1].Type != TokenType.OpCloseParen)
			return;
		
		var (source, range) = Tokens[start].SourceLocation;
		var parentheses = new SourceLocation(source, range.Join(Tokens[start + 1].SourceLocation.Range));
		Diagnostics.Add(new(DiagnosticSeverity.Hint, parentheses, "Redundant '()'")
		{
			Lint = Lints.Redundant,
			Fixes = [DiagnosticFix.RemoveExactly("Remove redundant '()'", parentheses)]
		});
	}
	
	private (ReceiverNode? Receiver, List<ParameterNode> Parameters, ITypeNode? ReturnType, bool IsVariadic)?
		ParseFunctionSignature(ref int index, bool allowVariadic, bool allowReceiver)
	{
		if (!Peek(index, TokenType.OpArrow) && !Peek(index, TokenType.OpFatArrow))
			SkipStray(ref index, TokenType.OpOpenParen);
		
		var start = index;
		if (ParseParameters(ref index, allowVariadic, allowReceiver, out var isVariadic, out var receiver) is not
		    { } parameters)
			return null;
		
		ReportEmptyParentheses(start, index);
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseRecoveredType(ref index) : null;
		return (receiver, parameters, returnType, isVariadic);
	}
	
	private ReceiverNode? ParseReceiver(ref int index)
	{
		var start = index;
		Token? mode = Match(ref index, out var keyword, TokenSets.ParameterModes) ? keyword : null;
		if (Match(ref index, out var self, TokenType.KeywordSelf))
			return new(mode, self);
		
		index = start;
		return null;
	}
	
	private List<ParameterNode>? ParseParameterList(ref int index, bool allowVariadic, out bool isVariadic)
	{
		isVariadic = false;
		var result = new List<ParameterNode>();
		if (allowVariadic && Peek(index, TokenType.OpDotDotDot) && !Peek(index + 1, TokenType.Identifier))
		{
			Report(Tokens[index], "Expected a parameter before '...'");
			return null;
		}
		
		if (StartsMemberDeclaration(index) || EndsParameterLine(index))
			return result;
		
		var errors = Diagnostics.ErrorCount;
		if (ParseParameter(ref index) is not { } firstParameter)
			return EndsParameterLine(index) ? result : null;
		
		result.Add(firstParameter);
		SkipBrokenParameter(ref index, errors);
		
		while (MatchSeparator(ref index, TokenType.OpCloseParen))
		{
			if (allowVariadic && !Peek(index + 1, TokenType.Identifier) && Match(ref index, TokenType.OpDotDotDot))
			{
				isVariadic = true;
				break;
			}
			
			if (StartsMemberDeclaration(index) || EndsParameterLine(index))
				break;
			
			errors = Diagnostics.ErrorCount;
			if (ParseParameter(ref index) is not { } parameter)
				return EndsParameterLine(index) ? result : null;
			
			result.Add(parameter);
			SkipBrokenParameter(ref index, errors);
		}
		
		return result;
	}
	
	private bool EndsParameterLine(int index) =>
		!IsOnSameLine(index) && (AtEnd(index) || Tokens[index].Type is TokenType.OpOpenBrace or
			TokenType.OpCloseBrace or TokenType.OpFatArrow or TokenType.OpArrow || StartsMemberDeclaration(index));
	
	private ParameterNode? ParseParameter(ref int index)
	{
		Token? mode = Match(ref index, out var keyword, TokenSets.ParameterModes) ? keyword : null;
		while (mode is not null && Match(ref index, out var extra, TokenSets.ParameterModes))
			Report(extra, $"Cannot combine '{mode.Value.Text}' and '{extra.Text}'");
		
		Token? ellipsis = Match(ref index, out var dots, TokenType.OpDotDotDot) ? dots : null;
		
		if (mode is not null && !IsOnSameLine(index) || !Match(ref index, out var identifier, TokenType.Identifier))
		{
			if (!IsOnSameLine(index))
				ReportMissing(index, "a parameter name");
			else if (IsReservedWord(index))
				Report(Tokens[index], DescribeReservedWord(Tokens[index]));
			else if (mode is { } named && ellipsis is null && Peek(index, TokenType.OpColon))
				Report(named, DescribeReservedWord(named));
			else
				ReportExpected(index, "a parameter name");
			
			return null;
		}
		
		if (!Match(ref index, TokenType.OpColon))
		{
			ReportMissing(index, "':' and a type");
			return new(mode, identifier, MissingType(index), null) { Ellipsis = ellipsis };
		}
		
		RejectBuiltInName(identifier);
		
		var type = ParseRecoveredType(ref index);
		
		if (!Match(ref index, TokenType.OpEqual))
			return new(mode, identifier, type, null) { Ellipsis = ellipsis };
		
		if (EndsListItem(index, TokenType.OpCloseParen))
		{
			ReportExpected(index - 1, "an expression");
			return new(mode, identifier, type, null) { Ellipsis = ellipsis };
		}
		
		var defaultValue = ParseExpression(ref index);
		return new(mode, identifier, type, defaultValue) { Ellipsis = ellipsis };
	}
	
	private void SkipBrokenParameter(ref int index, int errors)
	{
		if (Diagnostics.ErrorCount == errors)
			return;
		
		var depth = 0;
		for (var i = index; !AtEnd(i) && (i == index || !StartsDeclarationOnLine(i)); i++)
		{
			switch (Tokens[i].Type)
			{
				case TokenType.OpComma or TokenType.OpCloseParen when depth == 0:
					index = i;
					return;
				
				case TokenType.OpOpenParen or TokenType.OpOpenBracket:
					depth++;
					break;
				
				case TokenType.OpCloseParen or TokenType.OpCloseBracket:
					depth = Math.Max(depth - 1, 0);
					break;
				
				case TokenType.OpOpenBrace or TokenType.OpCloseBrace or TokenType.OpFatArrow when depth == 0:
					return;
			}
		}
	}
	
	private ITypeNode ParseRecoveredType(ref int index)
	{
		if (!IsOnSameLine(index))
		{
			ReportMissing(index, "a type");
			return MissingType(index);
		}
		
		try
		{
			return ParseType(ref index);
		}
		catch (ParseException e)
		{
			AddParseError(e);
			return MissingType(index);
		}
	}
	
	private MissingTypeNode MissingType(int index)
	{
		var (source, range) = Tokens[index - 1].SourceLocation;
		return new(new(source, new TextRange(range.End, range.End)));
	}
}