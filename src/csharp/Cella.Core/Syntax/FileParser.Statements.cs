using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private BlockStatementNode? ParseBlockStatement(ref int index, Token open)
	{
		// @TODO Diagnostics
		
		var statements = new List<IStatementNode>();
		if (!ParseStatements(ref index, open, statements, out var close))
			return null;
		
		var source = open.SourceLocation.Source;
		var range = open.SourceLocation.Range.Join(close.SourceLocation.Range);
		var sourceLocation = new SourceLocation(source, range);
		return new(sourceLocation, statements.ToImmutableArray());
	}
	
	private bool ParseStatements(ref int index, Token open, List<IStatementNode> statements, out Token close)
	{
		while (!Match(ref index, out close, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				ReportUnclosed(open);
				return false;
			}
			
			if (StartsDeclarationInBody(index))
			{
				ReportUnclosed(open);
				reachedDeclaration = true;
				return false;
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref i, branch, isActive) =>
					    ParseStatements(ref i, branch, isActive ? statements : [], out _)))
				{
					if (!reachedDeclaration)
						SkipToEnd(ref index);
					
					return false;
				}
				
				continue;
			}
			
			if (ParseStatement(ref index) is not { } statement)
			{
				if (!reachedDeclaration)
					SkipToEnd(ref index);
				
				return false;
			}
			
			statements.Add(statement);
		}
		
		return true;
	}
	
	private BlockStatementNode? ParseWhenStatement(ref int index, Token whenToken)
	{
		var statements = new List<IStatementNode>();
		return ParseWhen(ref index, (ref i, open, isActive) =>
			ParseStatements(ref i, open, isActive ? statements : [], out _))
			? new(Span(whenToken, Tokens[index - 1]), [..statements])
			: null;
	}
	
	private IStatementNode? ParseStatement(ref int index)
	{
		if (Peek(index, TokenType.OpCloseBrace))
		{
			ReportExpected(index, "a statement");
			return null;
		}
		
		if (Match(ref index, out var bindingToken, _bindingKeywords))
			return ParseVarStatement(ref index, bindingToken);
		
		if (Match(ref index, out var whenToken, TokenType.KeywordWhen))
			return ParseWhenStatement(ref index, whenToken);
		
		if (Match(ref index, out var ifToken, TokenType.KeywordIf))
			return ParseIfStatement(ref index, ifToken);
		
		if (Match(ref index, out var loopToken, _loopKeywords))
			return ParseLoop(ref index, loopToken, null);
		
		if (Match(ref index, out var matchToken, TokenType.KeywordMatch))
			return ParseMatchStatement(ref index, matchToken);
		
		if (Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
			return ParseBlockStatement(ref index, openBraceToken);
		
		if (Match(ref index, out var dropToken, TokenType.KeywordDrop))
			return ParseDropStatement(ref index, dropToken);
		
		if (index > 0 && !IsOnSameLine(index) && ExpressionParser.ContinuesExpression(Tokens[index].Type))
		{
			Report(Tokens[index], $"Cannot start a line with '{Tokens[index].Text}'");
			return null;
		}
		
		// Named loops
		var expr = ParseExpression(ref index);
		if (expr is not VarExpressionNode { Identifier.Type: TokenType.Identifier } var)
			return new ExpressionStatementNode(expr);
		
		// Attempt to parse as loop, else backtrack
		var backtrackIndex = index;
		
		if (IsOnSameLine(index) && Match(ref index, TokenType.OpColon))
		{
			if (Match(ref index, out var namedLoopToken, _loopKeywords))
				return ParseLoop(ref index, namedLoopToken, var.Identifier);
			
			ReportMissing(index, "a loop");
			return null;
		}
		
		index = backtrackIndex;
		return new ExpressionStatementNode(expr);
	}
	
	private DropStatementNode ParseDropStatement(ref int index, Token keyword)
	{
		var target = ParseExpression(ref index);
		var (source, range) = keyword.SourceLocation;
		return new(target, new(source, range.Join(target.SourceLocation.Range)));
	}
	
	private IStatementNode? ParseLoop(ref int index, Token keyword, Token? label) => keyword.Type switch
	{
		TokenType.KeywordFor => ParseForStatement(ref index, keyword, label),
		TokenType.KeywordWhile => ParseWhileStatement(ref index, keyword, label),
		TokenType.KeywordDo => ParseDoStatement(ref index, keyword, label),
		_ => ParseLoopStatement(ref index, keyword, label)
	};
	
	private static SourceLocation SpanLoop(Token keyword, Token? label, SourceLocation end)
	{
		var (source, range) = (label ?? keyword).SourceLocation;
		return new(source, range.Join(end.Range));
	}
	
	private ForStatementNode? ParseForStatement(ref int index, Token keyword, Token? label)
	{
		if (!Match(ref index, out var binding, TokenType.Identifier))
		{
			ReportExpected(index, "a name");
			return null;
		}
		
		if (!Match(ref index, TokenType.KeywordIn))
		{
			ReportExpected(index, "'in'");
			return null;
		}
		
		Token? mode = Match(ref index, out var mut, TokenType.KeywordMut) ? mut : null;
		if (StartsBody(index))
		{
			ReportMissing(index, "an expression");
			return null;
		}
		
		var source = ParseExpression(ref index);
		Token? rangeOperator = Match(ref index, out var op, _rangeOperators) ? op : null;
		if (rangeOperator is not null && StartsBody(index))
		{
			ReportMissing(index, "an expression");
			return null;
		}
		
		var end = rangeOperator is null ? null : ParseExpression(ref index);
		if (end is not null && Match(ref index, out var extraRange, _rangeOperators))
		{
			Report(extraRange, "Cannot chain ranges");
			return null;
		}
		
		return ParseStatement(ref index) is { } body
			? new(SpanLoop(keyword, label, body.SourceLocation), binding, mode, source, rangeOperator, end, body, label)
			: null;
	}
	
	private WhileStatementNode? ParseWhileStatement(ref int index, Token keyword, Token? label)
	{
		if (StartsBody(index))
		{
			ReportMissing(index, "a condition");
			return null;
		}
		
		var condition = ParseExpression(ref index);
		return ParseStatement(ref index) is { } body
			? new(SpanLoop(keyword, label, body.SourceLocation), condition, body, label)
			: null;
	}
	
	private DoWhileStatementNode? ParseDoStatement(ref int index, Token keyword, Token? label)
	{
		if (ParseStatement(ref index) is not { } body)
			return null;
		
		if (!Match(ref index, TokenType.KeywordWhile))
		{
			ReportMissing(index, "'while'");
			return null;
		}
		
		if (StartsBody(index))
		{
			ReportMissing(index, "a condition");
			return null;
		}
		
		var condition = ParseExpression(ref index);
		return new(SpanLoop(keyword, label, condition.SourceLocation), body, condition, label);
	}
	
	private bool StartsBody(int index) => !IsOnSameLine(index) || Peek(index, TokenType.OpOpenBrace);
	
	private void ReportMissing(int index, string expected) =>
		ReportExpected(IsOnSameLine(index) ? index : index - 1, expected);
	
	private LoopStatementNode? ParseLoopStatement(ref int index, Token keyword, Token? label) =>
		ParseStatement(ref index) is { } body ? new(SpanLoop(keyword, label, body.SourceLocation), body, label) : null;
	
	private IfStatementNode? ParseIfStatement(ref int index, Token ifToken)
	{
		// ifToken already consumed when this function is called
		
		if (StartsBody(index))
		{
			ReportMissing(index, "a condition");
			return null;
		}
		
		var condition = ParseExpression(ref index);
		
		if (ParseStatement(ref index) is not { } then)
			return null;
		
		var (source, range) = ifToken.SourceLocation;
		if (!Match(ref index, TokenType.KeywordElse))
		{
			range = range.Join(then.SourceLocation.Range);
			return new(new(source, range), condition, then, null);
		}
		
		if (ParseStatement(ref index) is not { } @else)
			return null;
		
		range = range.Join(@else.SourceLocation.Range);
		return new(new(source, range), condition, then, @else);
	}
	
	private MatchStatementNode? ParseMatchStatement(ref int index, Token matchToken)
	{
		Token? mode = Match(ref index, out var keyword, TokenType.KeywordMut) ? keyword : null;
		var value = ParseExpression(ref index);
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
		{
			ReportExpected(index, "'{'");
			return null;
		}
		
		var arms = new List<MatchArmNode>();
		if (ParseMatchArms(ref index, openBrace, arms) is not { } closeBrace)
			return null;
		
		var (source, range) = matchToken.SourceLocation;
		range = range.Join(closeBrace.SourceLocation.Range);
		return new(new(source, range), mode, value, arms);
	}
	
	private Token? ParseMatchArms(ref int index, Token openBrace, List<MatchArmNode> arms)
	{
		Token closeBrace;
		while (!Match(ref index, out closeBrace, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				ReportUnclosed(openBrace);
				return null;
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref i, open, isActive) =>
					    ParseMatchArms(ref i, open, isActive ? arms : []) is not null))
					return null;
				
				SeparateArms(ref index);
				continue;
			}
			
			if (ParseMatchArm(ref index) is not { } arm)
				return null;
			
			arms.Add(arm);
			SeparateArms(ref index);
		}
		
		return closeBrace;
	}
	
	private MatchArmNode? ParseMatchArm(ref int index)
	{
		List<PatternNode> patterns = [];
		IExpressionNode? guard = null;
		SourceLocation location;
		if (Match(ref index, out var elseToken, TokenType.KeywordElse))
			location = elseToken.SourceLocation;
		else
			(patterns, guard, location) = CreateExpressionParser().ParseArmHead(ref index);
		
		if (!Match(ref index, TokenType.OpFatArrow))
		{
			ReportExpected(index, "'=>'");
			return null;
		}
		
		return ParseStatement(ref index) is { } body ? new(patterns, guard, location, body) : null;
	}
	
	private VarStatementNode? ParseVarStatement(ref int index, Token keyword)
	{
		// keyword already consumed when this function is called
		
		// TODO Attempt resync 
		if (!Match(ref index, out var identifier, TokenType.Identifier))
		{
			Report(Tokens[index], Tokens[index].Error ?? (IsReservedWord(index)
				? DescribeReservedWord(Tokens[index])
				: $"Expected a name after '{keyword.Text}'"));
			
			return null;
		}
		
		ITypeNode? type = null;
		if (Match(ref index, TokenType.OpColon))
		{
			if (StartsMemberType(index + 1) &&
			    Match(ref index, out var visibility, _memberContextualKeywords, _visibilityKeywords))
				Report(visibility, $"Cannot use '{visibility.Text}' on local variables");
			
			type = ParseType(ref index);
		}
		
		var isMutable = keyword.Type == TokenType.KeywordVar;
		
		var (source, range) = keyword.SourceLocation;
		
		if (!Match(ref index, TokenType.OpEqual))
			return new(new(source, range), identifier, type, null, isMutable);
		
		var initializer = ParseExpression(ref index);
		range = range.Join(initializer.SourceLocation.Range);
		return new(new(source, range), identifier, type, initializer, isMutable);
	}
	
	private IExpressionNode ParseExpression(ref int index) => CreateExpressionParser().Parse(ref index);
	
	private ExpressionParser CreateExpressionParser() => new(Tokens)
	{
		ParseBlock = ParseBlockStatement,
		ParseWhenCondition = ParseCondition
	};
	
	private ITypeNode ParseType(ref int index) => new TypeParser(Tokens).Parse(ref index);
}