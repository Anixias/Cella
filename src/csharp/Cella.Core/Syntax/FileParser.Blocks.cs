using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private bool ParseTopLevelBlock(ref int index, Token openBrace, Token? block, List<IDeclarationNode> declarations)
	{
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				ReportUnclosed(openBrace);
				return false;
			}
			
			if (!StartsContextualName(index) &&
			    Match(ref index, out var useToken, _topLevelContextualKeywords, TokenType.KeywordUse))
			{
				Report(useToken, "Imports must precede all other declarations");
				if (ParseImportExpression(ref index) is null)
					ResyncTopLevel(ref index, true);
				
				continue;
			}
			
			if (ParseTopLevelDeclaration(ref index, declarations, block, true))
				continue;
			
			RecoverTopLevel(ref index, true);
		}
		
		return true;
	}
	
	private delegate bool BranchParser(ref int index, Token openBrace, bool isActive);
	
	private bool ParseWhen(ref int index, BranchParser parseBranch)
	{
		var taken = false;
		while (true)
		{
			if (ParseCondition(ref index) is not { } condition ||
			    !ParseBranch(ref index, parseBranch, condition && !taken))
				return false;
			
			taken |= condition;
			if (!Peek(index, TokenType.KeywordElse) || Peek(index + 1, TokenType.OpFatArrow))
				return true;
			
			Match(ref index, TokenType.KeywordElse);
			
			if (!Match(ref index, TokenType.KeywordWhen))
				return ParseBranch(ref index, parseBranch, !taken);
		}
	}
	
	private bool ParseBranch(ref int index, BranchParser parseBranch, bool isActive)
	{
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			return parseBranch(ref index, openBrace, isActive);
		
		ReportExpected(index, "'{'");
		return false;
	}
	
	public bool? ParseCondition()
	{
		var index = 0;
		if (ParseCondition(ref index) is not { } value)
			return null;
		
		if (AtEnd(index))
			return value;
		
		ReportUnexpected(Tokens[index]);
		return null;
	}
	
	private bool? ParseCondition(ref int index)
	{
		if (ParseConjunction(ref index) is not { } value)
			return null;
		
		while (Match(ref index, TokenType.OpBarBar))
		{
			if (ParseConjunction(ref index) is not { } other)
				return null;
			
			value |= other;
		}
		
		return value;
	}
	
	private bool? ParseConjunction(ref int index)
	{
		if (ParseFlag(ref index) is not { } value)
			return null;
		
		while (Match(ref index, TokenType.OpAmpersandAmpersand))
		{
			if (ParseFlag(ref index) is not { } other)
				return null;
			
			value &= other;
		}
		
		return value;
	}
	
	private bool? ParseFlag(ref int index)
	{
		if (index > 0 && !IsOnSameLine(index) &&
		    Tokens[index - 1].Type is not (TokenType.OpAmpersandAmpersand or TokenType.OpBarBar))
		{
			ReportExpected(index - 1, "a flag");
			return null;
		}
		
		if (Match(ref index, TokenType.OpBang))
			return !ParseFlag(ref index);
		
		if (Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			var value = ParseCondition(ref index);
			if (value is null || Match(ref index, TokenType.OpCloseParen))
				return value;
			
			ReportExpected(index, "')'", openParen);
			return null;
		}
		
		if (!Match(ref index, out var name, TokenType.Identifier))
		{
			ReportExpected(index, "a flag");
			return null;
		}
		
		if (flags.TryGetValue(name.Text, out var flag))
			return flag;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, $"Flag '{name.Text}' not found")
		{
			Hints = DiagnosticReporter.FindSuggestion(name.Text, flags.Keys) is { } hint ? [hint] : []
		});
		
		return false;
	}
	
	private bool IsVisibilityBlock(int index, IReadOnlyDictionary<string, TokenType> keywords) =>
		MatchVisibilityBlock(ref index, keywords, out _, out _) || StartsVisibleExternals(index);
	
	private bool StartsVisibleExternals(int index) =>
		(Match(ref index, _topLevelContextualKeywords, _blockKeywords) ||
		 Match(ref index, _topLevelContextualKeywords, TokenType.KeywordPub)) &&
		Match(ref index, _topLevelContextualKeywords, TokenType.KeywordExt) &&
		(Peek(index, TokenType.OpOpenBrace) || IsOnSameLine(index) && (Peek(index, TokenType.OpOpenParen) ||
		                                                               Peek(index, TokenType.Identifier) &&
		                                                               Peek(index + 1, TokenType.OpColon)));
	
	private bool MatchPubBlock(ref int index, IReadOnlyDictionary<string, TokenType> keywords, out Token keyword,
		out Token openBrace)
	{
		var start = index;
		if (Match(ref index, out keyword, keywords, TokenType.KeywordPub) &&
		    Match(ref index, out openBrace, TokenType.OpOpenBrace))
			return true;
		
		index = start;
		openBrace = default;
		return false;
	}
	
	private bool MatchVisibilityBlock(ref int index, IReadOnlyDictionary<string, TokenType> keywords, out Token keyword,
		out Token openBrace)
	{
		var start = index;
		if (Match(ref index, out keyword, keywords, _blockKeywords) &&
		    Match(ref index, out openBrace, TokenType.OpOpenBrace))
			return true;
		
		index = start;
		openBrace = default;
		return false;
	}
}