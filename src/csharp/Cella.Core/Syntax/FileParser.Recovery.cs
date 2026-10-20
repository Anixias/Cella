using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private void RecoverTopLevel(ref int index, bool insideBlock)
	{
		var token = Tokens[index];
		var startsLine = index == 0 || token.Line != Tokens[index - 1].Line;
		var namesDeclaration = startsLine && Peek(index + 1, TokenType.OpColon);
		if (token.Text == "pvt" && !namesDeclaration)
		{
			Report(token, "Expected '{' after 'pvt'");
			index++;
			return;
		}
		
		if (token.Text == "pvt")
			Report(token, "Expected '{' after 'pvt'");
		else if (namesDeclaration && _operatorNames.Contains(token.Type))
			Report(token, "Cannot declare operators outside types");
		else if (namesDeclaration && token.Type == TokenType.KeywordNew)
			Report(token, "Cannot declare constructors outside types");
		else if (namesDeclaration && token.Type == TokenType.KeywordDrop)
			Report(token, "Cannot declare destructors outside types");
		else if (namesDeclaration && IsReservedWord(index))
			Report(token, DescribeReservedWord(token));
		else if (startsLine && !namesDeclaration && _declarationKinds.Contains(token.Text))
			Report(token, $"Expected a name before '{token.Text}'");
		else
			ReportUnexpected(token);
		
		index++;
		while (IsOnSameLine(index) && !Peek(index, TokenType.OpOpenBrace) && !Peek(index, TokenType.OpCloseBrace))
			index++;
		
		ResyncTopLevel(ref index, insideBlock);
	}
	
	private void ResyncTopLevel(ref int index, bool insideBlock = false)
	{
		// Skip until another declaration starts. If we encounter braces, keep skipping until closed
		var braceCount = 0;
		var groupCount = 0;
		var loop = true;
		while (loop && !AtEnd(index))
		{
			switch (Tokens[index].Type)
			{
				case TokenType.OpOpenBrace:
					braceCount++;
					index++;
					break;
				
				case TokenType.OpCloseBrace when braceCount == 0 && insideBlock:
					loop = false;
					break;
				
				case TokenType.OpCloseBrace:
					braceCount--;
					index++;
					break;
				
				case TokenType.KeywordWhen when braceCount == 0:
					loop = false;
					break;
				
				case TokenType.OpOpenParen or TokenType.OpOpenBracket:
					groupCount++;
					index++;
					break;
				
				case TokenType.OpCloseParen or TokenType.OpCloseBracket:
					groupCount = Math.Max(groupCount - 1, 0);
					index++;
					break;
				
				case TokenType.Identifier:
					if (braceCount > 0 || groupCount > 0 ||
					    !StartsTopLevelDeclaration(index) && !StartsTopLevelForm(index))
						index++;
					else
						loop = false;
					
					break;
				
				default:
					if (braceCount == 0 && groupCount == 0 && StartsTopLevelForm(index))
						loop = false;
					else
						index++;
					
					break;
			}
		}
	}
	
	private bool StartsTopLevelForm(int index)
	{
		if (index == 0 || Tokens[index].Line == Tokens[index - 1].Line)
			return false;
		
		var token = Tokens[index];
		return _declarationKinds.Contains(token.Text) || _topLevelFormWords.Contains(token.Text) ||
		       Peek(index + 1, TokenType.OpColon) && (_operatorNames.Contains(token.Type) ||
		                                              _lifecycleKeywords.Contains(token.Type)) ||
		       LookAhead(() => FindUnnamedImplTarget(index) is not null);
	}
	
	private bool StartsTopLevelDeclaration(int index)
	{
		var keywordIndex = index;
		return Peek(index + 1, TokenType.OpColon) ||
		       Peek(index + 1, TokenType.OpOpenBracket) && IsOnSameLine(index + 1) ||
		       LookAhead(() => Match(ref keywordIndex, _topLevelContextualKeywords, _topLevelKeywords));
	}
	
	private bool StartsDeclarationInBody(int index) =>
		Tokens[index].Type == TokenType.Identifier && Peek(index + 1, TokenType.OpColon) && !AtEnd(index + 2) &&
		_bodyDeclarationWords.Contains(Tokens[index + 2].Text) && Tokens[index].Line != Tokens[index - 1].Line &&
		IsOnSameLine(index + 2);
	
	private void SkipDeclaration(ref int index, int start, bool insideBlock = false)
	{
		if (index > start && !AtEnd(index) && StartsDeclarationInBody(index))
			return;
		
		var braceCount = 0;
		var parameterLists = new Stack<bool>();
		for (var i = start; !AtEnd(i); i++)
		{
			switch (Tokens[i].Type)
			{
				case TokenType.OpOpenBrace:
					braceCount++;
					break;
				
				case TokenType.OpCloseBrace:
					braceCount--;
					if (braceCount > 0)
						break;
					
					index = braceCount < 0 && insideBlock ? i : i + 1;
					return;
				
				case TokenType.OpOpenParen:
					parameterLists.Push(FollowsFun(i));
					break;
				
				case TokenType.OpCloseParen:
					parameterLists.TryPop(out _);
					break;
				
				case TokenType.Identifier when i > start && braceCount == 0 && !parameterLists.Contains(true) &&
				                               StartsDeclarationOnLine(i):
					index = i;
					return;
			}
		}
		
		ResyncTopLevel(ref index, insideBlock);
	}
	
	private bool FollowsFun(int index)
	{
		var keywordIndex = index - 1;
		return Match(ref keywordIndex, TokenType.KeywordFun);
	}
	
	private bool StartsDeclarationOnLine(int index) =>
		Tokens[index].Line != Tokens[index - 1].Line && StartsTopLevelDeclaration(index);
	
	private bool ResyncStatement(ref int index, int start)
	{
		var braces = 0;
		var brackets = 0;
		int? bracesAtError = null;
		for (var i = start + 1; !AtEnd(i); i++)
		{
			CountGroup(Tokens[i - 1].Type, ref braces, ref brackets);
			if (i < index)
				continue;
			
			bracesAtError ??= braces;
			if (!IsOnSameLine(i) && StartsDeclarationInBody(i) && (braces > 0 || bracesAtError > 0))
			{
				index = i;
				return true;
			}
			
			if (i == index && braces > 0 && !IsOnSameLine(i) && TokenSets.StatementKeywords.Contains(Tokens[i].Type))
				return false;
			
			if (braces > 0)
				continue;
			
			if (Tokens[i].Type == TokenType.OpCloseBrace ||
			    !IsOnSameLine(i) && Tokens[i].Type != TokenType.OpOpenBrace &&
			    (brackets <= 0 || TokenSets.StatementKeywords.Contains(Tokens[i].Type)))
			{
				index = i;
				return false;
			}
		}
		
		SkipUntil(ref index, TokenType.EndOfFile);
		return false;
	}
	
	private bool ResyncMember(ref int index, int start)
	{
		droppedMembers++;
		var braces = 0;
		var brackets = 0;
		int? bracesAtError = null;
		for (var i = start + 1; !AtEnd(i); i++)
		{
			CountGroup(Tokens[i - 1].Type, ref braces, ref brackets);
			if (i < index)
				continue;
			
			bracesAtError ??= braces;
			if (braces < 0 || braces == 0 && Tokens[i].Type == TokenType.OpCloseBrace)
			{
				index = i;
				return braces < 0;
			}
			
			if (IsOnSameLine(i))
				continue;
			
			if (braces <= bracesAtError && StartsDeclarationInBody(i) ||
			    braces == 0 && brackets <= 0 && Tokens[i].Type != TokenType.OpOpenBrace &&
			    !ExpressionParser.ContinuesExpression(Tokens[i].Type))
			{
				index = i;
				return false;
			}
		}
		
		SkipUntil(ref index, TokenType.EndOfFile);
		return false;
	}
	
	private static void CountGroup(TokenType type, ref int braces, ref int brackets)
	{
		switch (type)
		{
			case TokenType.OpOpenBrace:
				braces++;
				break;
			
			case TokenType.OpCloseBrace:
				braces--;
				break;
			
			case TokenType.OpOpenParen or TokenType.OpOpenBracket:
				brackets++;
				break;
			
			case TokenType.OpCloseParen or TokenType.OpCloseBracket:
				brackets--;
				break;
		}
	}
	
	private void ReportUnclosed(Token openBrace) => Report(openBrace, "Expected '}' to close this '{'");
}