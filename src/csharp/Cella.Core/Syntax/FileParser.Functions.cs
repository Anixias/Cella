using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private FunctionNode? ParseFunction(ref int index, Token identifier, DeclarationModifiers modifiers,
		bool isExternal, List<TypeParameterNode> typeParameters, Token? externalName = null)
	{
		// When this is called, the identifier and fun keyword are already consumed
		// Caller is expected to resync in case of errors
		
		// @TODO Diagnostics
		
		if (ParseFunctionSignature(ref index, false, true) is not { } signature)
			return null;
		
		var (receiver, parameters, returnType, _) = signature;
		WhenClause? when = null;
		if (IsOnSameLine(index) && Match(ref index, out var whenKeyword, TokenType.KeywordWhen))
		{
			if (ParseCondition(ref index) is not { } condition)
				return null;
			
			when = new(whenKeyword, condition);
		}
		
		if (Match(ref index, TokenType.OpEqual))
		{
			if (ParseExpressionBody(ref index, returnType is null) is not { } statement)
				return null;
			
			return new(identifier, modifiers.Tokens, receiver, parameters, returnType, statement, isExternal)
			{
				Visibility = modifiers.Visibility,
				TypeParameters = [..typeParameters],
				When = when,
				ExternalName = externalName
			};
		}
		
		if (!Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
		{
			if (allowsMissingBodies)
				return new(identifier, modifiers.Tokens, receiver, parameters, returnType, null, isExternal)
				{
					Visibility = modifiers.Visibility,
					TypeParameters = [..typeParameters],
					When = when,
					ExternalName = externalName
				};
			
			ReportExpected(IsOnSameLine(index) ? index : index - 1, "'=' or '{'");
			return null;
		}
		
		if (ParseBlockStatement(ref index, openBraceToken) is not { } body)
			return null;
		
		return new(identifier, modifiers.Tokens, receiver, parameters, returnType, body, isExternal)
		{
			Visibility = modifiers.Visibility,
			ExternalName = externalName,
			TypeParameters = [..typeParameters],
			When = when
		};
	}
	
	private IStatementNode? ParseExpressionBody(ref int index, bool discardsValue)
	{
		if (discardsValue && Match(ref index, out var matchToken, TokenType.KeywordMatch))
			return ParseMatchStatement(ref index, matchToken);
		
		var expression = ParseExpression(ref index);
		return new ExpressionStatementNode(discardsValue
			? expression
			: new ReturnExpressionNode(expression.SourceLocation, expression));
	}
	
	private GlobalNode? ParseGlobal(ref int index, Token identifier, DeclarationModifiers modifiers, Token keyword,
		bool isExternal = false, Token? externalName = null)
	{
		var type = ParseType(ref index);
		if (!Match(ref index, TokenType.OpEqual))
		{
			Report(type.SourceLocation, "Expected '=' and an initial value");
			return null;
		}
		
		var initializer = ParseExpression(ref index);
		return new(identifier, keyword, type, initializer)
		{
			Visibility = modifiers.Visibility,
			WriteVisibility = modifiers.WriteVisibility,
			IsExternal = isExternal,
			ExternalName = externalName
		};
	}
	
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
		
		return name;
	}
	
	private GlobalNode? ParseStaticField(ref int index, Token identifier, DeclarationModifiers modifiers, Token keyword)
	{
		if (keyword.Type == TokenType.KeywordVal)
			RejectWriteRestriction(modifiers, "'val' fields");
		
		return ParseDeclaration(ref index, identifier, "field",
			(ref i) => ParseGlobal(ref i, identifier, modifiers, keyword));
	}
	
	private IDeclarationNode? ParseExternalDeclaration(ref int index, string? origin, Token? block)
	{
		// TODO Diagnostics
		
		Token identifier;
		if (StartsContextualName(index))
			identifier = Tokens[index++];
		else if (!Match(ref index, out identifier, _topLevelContextualKeywords, TokenType.Identifier))
		{
			if (IsReservedWord(index))
				Report(Tokens[index], DescribeReservedWord(Tokens[index]));
			
			return null;
		}
		
		if (!RejectTypeParameters(ref index, "ext functions") || !Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseDeclarationModifiers(ref index, block);
		Token? externalName = null;
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordExt))
		{
			externalName = ParseExternalName(ref index);
			if (externalName is null)
				return null;
		}
		
		if (Match(ref index, out var keyword, _bindingKeywords))
		{
			var type = ParseType(ref index);
			if (Match(ref index, out var equals, TokenType.OpEqual))
			{
				Report(equals, "Cannot initialize values in 'ext' blocks");
				ParseExpression(ref index);
			}
			
			return new GlobalNode(identifier, keyword, type, null)
			{
				Visibility = modifiers.Visibility,
				WriteVisibility = modifiers.WriteVisibility,
				IsExternal = true,
				ExternalName = externalName
			};
		}
		
		if (!Match(ref index, TokenType.KeywordFun))
			return null;
		
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
	
	private List<ParameterNode>? ParseParameters(ref int index, bool optionalParentheses) =>
		ParseParameters(ref index, optionalParentheses, false, false, out _, out _);
	
	private List<ParameterNode>? ParseParameters(ref int index, bool optionalParentheses, bool allowVariadic,
		bool allowReceiver, out bool isVariadic, out ReceiverNode? receiver)
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
				if (Peek(index, TokenType.OpColon))
					Report(Tokens[index], "Cannot declare a type for 'self'");
				else
					ReportExpected(index, "',' or ')'", openParen);
				
				return null;
			}
			
			if (ParseParameterList(ref index, allowVariadic, out isVariadic) is not { } parameterList)
				return null;
			
			parameters.AddRange(parameterList);
			if (!Match(ref index, TokenType.OpCloseParen))
			{
				ReportExpected(index, "',' or ')'", openParen);
				return null;
			}
		}
		else if (!optionalParentheses)
			return null; // TODO Diagnostics
		
		return parameters;
	}
	
	private (ReceiverNode? Receiver, List<ParameterNode> Parameters, ITypeNode? ReturnType, bool IsVariadic)?
		ParseFunctionSignature(ref int index, bool allowVariadic, bool allowReceiver)
	{
		if (ParseParameters(ref index, true, allowVariadic, allowReceiver, out var isVariadic, out var receiver) is not
		    { } parameters)
			return null;
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
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
		
		if (ParseParameter(ref index) is not { } firstParameter)
			return null;
		
		result.Add(firstParameter);
		
		while (Match(ref index, TokenType.OpComma))
		{
			if (allowVariadic && !Peek(index + 1, TokenType.Identifier) && Match(ref index, TokenType.OpDotDotDot))
			{
				isVariadic = true;
				break;
			}
			
			if (ParseParameter(ref index) is not { } parameter)
				return null;
			
			result.Add(parameter);
		}
		
		return result;
	}
	
	private ParameterNode? ParseParameter(ref int index)
	{
		// TODO Diagnostics
		
		Token? mode = Match(ref index, out var keyword, TokenSets.ParameterModes) ? keyword : null;
		while (mode is not null && Match(ref index, out var extra, TokenSets.ParameterModes))
			Report(extra, $"Cannot combine '{mode.Value.Text}' and '{extra.Text}'");
		
		Token? ellipsis = Match(ref index, out var dots, TokenType.OpDotDotDot) ? dots : null;
		
		// TODO Attempt resync 
		if (!Match(ref index, out var identifier, TokenType.Identifier))
		{
			if (IsReservedWord(index))
				Report(Tokens[index], DescribeReservedWord(Tokens[index]));
			else if (mode is { } named && ellipsis is null && Peek(index, TokenType.OpColon))
				Report(named, DescribeReservedWord(named));
			else
				ReportExpected(index, "a parameter name");
			
			return null;
		}
		
		if (!Match(ref index, TokenType.OpColon))
		{
			ReportExpected(index, "':' and a type");
			return null;
		}
		
		var type = ParseType(ref index);
		
		if (!Match(ref index, TokenType.OpEqual))
			return new(mode, identifier, type, null) { Ellipsis = ellipsis };
		
		var defaultValue = ParseExpression(ref index);
		return new(mode, identifier, type, defaultValue) { Ellipsis = ellipsis };
	}
}
