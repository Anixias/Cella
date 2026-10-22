using Cella.Core.Binding;
using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private BlockStatementNode? ParseBlockStatement(ref int index, Token open)
	{
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
		blockDepth++;
		try
		{
			return ParseBlockContents(ref index, open, statements, out close);
		}
		finally
		{
			blockDepth--;
		}
	}
	
	private bool ParseBlockContents(ref int index, Token open, List<IStatementNode> statements, out Token close)
	{
		while (!Match(ref index, out close, TokenType.OpCloseBrace))
		{
			if (AtEnd(index) || StartsDeclarationInBody(index) || StartsMemberDeclaration(index))
			{
				ReportUnclosed(open);
				close = Tokens[index - 1];
				return true;
			}
			
			var start = index;
			RecordSite(index, CompletionSite.Statement);
			var errorsBefore = Diagnostics.ErrorCount;
			var attributedBefore = attributedErrors;
			IStatementNode? statement = null;
			int? cut = null;
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (ParseWhen(ref index, (ref i, branch, isActive) =>
					    ParseStatements(ref i, branch, isActive ? statements : [], out _)))
					continue;
			}
			else
			{
				var state = SaveState(index);
				statement = TryParseStatement(ref index);
				var broken = statement is null || HasLexicalErrors(start, index) ||
				             Diagnostics.ErrorCount - errorsBefore > attributedErrors - attributedBefore;
				
				var skipped = index;
				if (statement is null)
					ResyncStatement(ref skipped, start);
				
				var original = statement;
				cut = broken
					? FindCut(start, Math.Max(index, skipped), statement is null, (i, _, _) =>
						StartsStatementCut(i) && !(original is not null &&
						                           HasNestedStatementAt(original,
							                           Tokens[i].SourceLocation.Range.Start)))
					: null;
				
				if (cut is not null)
				{
					RestoreState(state, index);
					index = start;
					var tokens = Bound(cut.Value);
					try
					{
						statement = TryParseStatement(ref index);
					}
					finally
					{
						Unbind(tokens);
					}
				}
			}
			
			if (statement is null)
			{
				var resynced = false;
				if (cut is not null)
					index = cut.Value;
				else
					resynced = ResyncStatement(ref index, start);
				
				ReportLexicalErrors(start, index);
				AddBrokenStatement(statements, start, Math.Max(index - 1, start));
				attributedErrors = attributedBefore + (Diagnostics.ErrorCount - errorsBefore);
				if (!resynced)
					continue;
				
				close = Tokens[index - 1];
				return true;
			}
			
			ReportLexicalErrors(start, index);
			if (Diagnostics.ErrorCount - errorsBefore > attributedErrors - attributedBefore)
				_brokenStatements.Add(statement);
			
			attributedErrors = attributedBefore + (Diagnostics.ErrorCount - errorsBefore);
			statements.Add(statement);
		}
		
		return true;
	}
	
	private IStatementNode? TryParseStatement(ref int index)
	{
		try
		{
			return ParseStatement(ref index);
		}
		catch (ParseException e) when (!e.IsNesting || blockDepth == 1)
		{
			AddParseError(e);
			return null;
		}
	}
	
	private void AddBrokenStatement(List<IStatementNode> statements, int start, int end)
	{
		var extent = Span(Tokens[start], Tokens[end]);
		var declares = _bindingKeywords.Contains(Tokens[start].Type) && start + 1 <= end &&
		               Tokens[start + 1].Type == TokenType.Identifier;
		
		var broken = new BrokenStatementNode(extent, declares ? Tokens[start + 1] : null,
			Tokens[start].Type == TokenType.KeywordVar);
		
		if (statements is [.., var previous] && start > 0 &&
		    (IsOnSameLine(start) || ExpressionParser.ContinuesExpression(Tokens[start].Type)))
		{
			statements.RemoveAt(statements.Count - 1);
			var (source, range) = previous.SourceLocation;
			extent = new(source, range.Join(extent.Range));
			broken = previous switch
			{
				VarStatementNode declaration => new(extent, declaration.Identifier, declaration.IsMutable),
				BrokenStatementNode earlier => new(extent, earlier.Name, earlier.IsMutable),
				_ => new(extent, null, false)
			};
			
			_brokenStatements.Remove(previous);
		}
		
		_brokenStatements.Add(broken);
		statements.Add(broken);
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
		EnsureStack(index);
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
			index++;
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
	
	private DropStatementNode? ParseDropStatement(ref int index, Token keyword)
	{
		if (!IsOnSameLine(index))
		{
			ReportExpected(index - 1, "an expression");
			return null;
		}
		
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
		Token? bindingMode = Match(ref index, out var modeKeyword, TokenSets.BorrowKeywords) ? modeKeyword : null;
		
		if (!Match(ref index, out var binding, TokenType.Identifier))
		{
			ReportExpected(index, "a name");
			return null;
		}
		
		if (!Match(ref index, out var inKeyword, TokenType.KeywordIn))
		{
			ReportMissing(index, "'in'");
			return IsOnSameLine(index) ? null : IncompleteFor(ref index, keyword, label, binding, bindingMode, null);
		}
		
		Token? mode = Match(ref index, out var mut, TokenType.KeywordMut) ? mut : null;
		if (StartsBody(index))
		{
			ReportMissing(index, "an expression");
			return IncompleteFor(ref index, keyword, label, binding, bindingMode, inKeyword);
		}
		
		var source = ParseExpression(ref index);
		Token? rangeOperator = Match(ref index, out var op, _rangeOperators) ? op : null;
		if (rangeOperator is not null && StartsBody(index))
		{
			ReportMissing(index, "an expression");
			var missingEnd = Missing(index);
			return ParseIncompleteBody(ref index) is { } rangeBody
				? new(SpanLoop(keyword, label, rangeBody.SourceLocation), binding, mode, source, rangeOperator,
					missingEnd, rangeBody, label)
				{
					Keyword = keyword,
					In = inKeyword,
					BindingMode = bindingMode
				}
				: null;
		}
		
		var end = rangeOperator is null ? null : ParseExpression(ref index);
		if (end is not null && Match(ref index, out var extraRange, _rangeOperators))
		{
			Report(extraRange, "Cannot chain ranges");
			return null;
		}
		
		return ParseBody(ref index) is { } body
			? new(SpanLoop(keyword, label, body.SourceLocation), binding, mode, source, rangeOperator, end, body, label)
			{
				Keyword = keyword,
				In = inKeyword,
				BindingMode = bindingMode
			}
			: null;
	}
	
	private ForStatementNode? IncompleteFor(ref int index, Token keyword, Token? label, Token binding,
		Token? bindingMode, Token? inKeyword)
	{
		var source = Missing(index);
		return ParseIncompleteBody(ref index) is { } body
			? new(SpanLoop(keyword, label, body.SourceLocation), binding, null, source, null, null, body, label)
			{
				Keyword = keyword,
				In = inKeyword,
				BindingMode = bindingMode
			}
			: null;
	}
	
	private IStatementNode? ParseIncompleteBody(ref int index) =>
		AtEnd(index) || Peek(index, TokenType.OpCloseBrace) || StartsDeclarationStatement(index)
			? EmptyBody(index)
			: ParseBody(ref index);
	
	private MissingExpressionNode Missing(int index)
	{
		var (source, range) = Tokens[index - 1].SourceLocation;
		return new(new(source, new TextRange(range.End, range.End)));
	}
	
	private BlockStatementNode EmptyBody(int index)
	{
		var (source, range) = Tokens[index - 1].SourceLocation;
		return new(new(source, new TextRange(range.End, range.End)), []);
	}
	
	private IStatementNode? ParseConditionBody(ref int index, IExpressionNode condition) =>
		ExpressionParser.EndsIncomplete(condition) && StartsDeclarationStatement(index)
			? EmptyBody(index)
			: ParseBody(ref index);
	
	private bool StartsDeclarationStatement(int index) =>
		!IsOnSameLine(index) && Tokens[index].Type is TokenType.KeywordVal or TokenType.KeywordVar or
			TokenType.KeywordConst;
	
	private WhileStatementNode? ParseWhileStatement(ref int index, Token keyword, Token? label)
	{
		if (StartsBody(index))
		{
			ReportMissing(index, "a condition");
			var missing = Missing(index);
			return ParseIncompleteBody(ref index) is { } incomplete
				? new(SpanLoop(keyword, label, incomplete.SourceLocation), missing, incomplete, label)
				: null;
		}
		
		var condition = ParseExpression(ref index);
		return ParseConditionBody(ref index, condition) is { } body
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
	
	private IStatementNode? ParseBody(ref int index)
	{
		var start = index;
		var attributedBefore = attributedErrors;
		var attempt = new List<Diagnostic>();
		var outer = Diagnostics.Redirect(attempt);
		IStatementNode? body = null;
		ParseException? failure = null;
		try
		{
			body = ParseStatement(ref index);
		}
		catch (ParseException e) when (!e.IsNesting)
		{
			failure = e;
		}
		finally
		{
			Diagnostics.Redirect(outer);
		}
		
		var keepsAttempt = body is not null || Peek(start, TokenType.OpOpenBrace) ||
		                   Peek(start, TokenType.OpCloseBrace) || AtEnd(start);
		
		if (keepsAttempt)
		{
			Diagnostics.AddRange(attempt);
			if (body is not null || Peek(start, TokenType.OpOpenBrace))
				return failure is null ? body : throw failure;
			
			if (failure is not null)
				AddParseError(failure);
		}
		else
			attributedErrors = attributedBefore;
		
		index = start;
		var (source, range) = Tokens[start - 1].SourceLocation;
		return new BrokenStatementNode(new(source, new(range.End, range.End)), null, false);
	}
	
	private bool StartsBody(int index) => !IsOnSameLine(index) || Peek(index, TokenType.OpOpenBrace);
	
	private void ReportMissing(int index, string expected) =>
		ReportExpected(IsOnSameLine(index) ? index : index - 1, expected);
	
	private LoopStatementNode? ParseLoopStatement(ref int index, Token keyword, Token? label) =>
		ParseBody(ref index) is { } body ? new(SpanLoop(keyword, label, body.SourceLocation), body, label) : null;
	
	private IfStatementNode? ParseIfStatement(ref int index, Token ifToken)
	{
		// ifToken already consumed when this function is called
		
		var arms = new List<(Token Keyword, IExpressionNode Condition, IStatementNode Then)>();
		IStatementNode? @else = null;
		var keyword = ifToken;
		while (true)
		{
			IExpressionNode condition;
			IStatementNode? then;
			if (StartsBody(index))
			{
				ReportMissing(index, "a condition");
				condition = Missing(index);
				then = ParseIncompleteBody(ref index);
			}
			else
			{
				condition = ParseExpression(ref index);
				then = ParseConditionBody(ref index, condition);
			}
			
			if (then is null)
				return null;
			
			arms.Add((keyword, condition, then));
			if (!Match(ref index, TokenType.KeywordElse))
				break;
			
			if (Match(ref index, out keyword, TokenType.KeywordIf))
				continue;
			
			if (ParseBody(ref index) is not { } last)
				return null;
			
			@else = last;
			break;
		}
		
		IfStatementNode? chain = null;
		for (var i = arms.Count - 1; i >= 0; i--)
		{
			var (source, range) = arms[i].Keyword.SourceLocation;
			var tail = (IStatementNode?)chain ?? @else;
			range = range.Join((tail ?? arms[i].Then).SourceLocation.Range);
			chain = new IfStatementNode(new(source, range), arms[i].Condition, arms[i].Then, tail);
		}
		
		return chain;
	}
	
	private MatchStatementNode? ParseMatchStatement(ref int index, Token matchToken)
	{
		Token? mode = Match(ref index, out var keyword, TokenType.KeywordMut) ? keyword : null;
		var value = ParseExpression(ref index);
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
		{
			if (IsOnSameLine(index))
			{
				ReportExpected(index, "'{'");
				return null;
			}
			
			ReportExpected(index - 1, "'{'");
			var (start, end) = matchToken.SourceLocation;
			return new(new(start, end.Join(value.SourceLocation.Range)), mode, value, []);
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
				return Tokens[index - 1];
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref i, open, isActive) =>
					    ParseMatchArms(ref i, open, isActive ? arms : []) is not null))
					return null;
				
				SeparateArms(ref index);
				continue;
			}
			
			if (TokenSets.StatementKeywords.Contains(Tokens[index].Type))
			{
				ReportExpected(index, "'}'", openBrace);
				return null;
			}
			
			var armStart = index;
			MatchArmNode? arm;
			try
			{
				arm = ParseMatchArm(ref index);
			}
			catch (ParseException e) when (!e.IsNesting)
			{
				AddParseError(e);
				arm = null;
			}
			
			if (arm is null)
			{
				index = FindNextArm(armStart);
				continue;
			}
			
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
			ReportMissing(index, "'=>'");
			if (IsOnSameLine(index) && !Peek(index, TokenType.OpCloseBrace))
				return null;
			
			var (source, range) = Tokens[index - 1].SourceLocation;
			var missing = new BrokenStatementNode(new(source, new(range.End, range.End)), null, false);
			_brokenStatements.Add(missing);
			return new(patterns, guard, location, missing);
		}
		
		return ParseStatement(ref index) is { } body ? new(patterns, guard, location, body) : null;
	}
	
	private VarStatementNode? ParseVarStatement(ref int index, Token keyword)
	{
		// keyword already consumed when this function is called
		
		SkipStray(ref index, TokenType.Identifier);
		if (!IsOnSameLine(index))
		{
			Report(keyword, $"Expected a name after '{keyword.Text}'");
			return null;
		}
		
		if (!Match(ref index, out var identifier, TokenType.Identifier))
		{
			Report(Tokens[index], Tokens[index].Error ?? (IsReservedWord(index)
				? DescribeReservedWord(Tokens[index])
				: $"Expected a name after '{keyword.Text}'"));
			
			return null;
		}
		
		var isMutable = keyword.Type == TokenType.KeywordVar;
		var (source, range) = keyword.SourceLocation;
		var isConstant = keyword.Type == TokenType.KeywordConst;
		if (!SkipStray(ref index, TokenType.OpColon))
			SkipStray(ref index, TokenType.OpEqual);
		
		ITypeNode? type = null;
		if (Match(ref index, out var colon, TokenType.OpColon))
		{
			if (!IsOnSameLine(index))
			{
				Report(colon, "Expected a type after ':'");
				return new(new(source, range), identifier, null, null, isMutable)
				{
					IsConstant = isConstant,
					Keyword = keyword
				};
			}
			
			if (StartsMemberType(index + 1) &&
			    Match(ref index, out var visibility, _memberContextualKeywords, _visibilityKeywords))
				Report(visibility, $"Cannot use '{visibility.Text}' on local variables");
			
			type = ParseType(ref index);
		}
		
		if (!MatchOnLine(ref index, TokenType.OpEqual))
			return new(new(source, range), identifier, type, null, isMutable)
			{
				IsConstant = isConstant,
				Keyword = keyword
			};
		
		var initializer = ParseExpression(ref index);
		range = range.Join(initializer.SourceLocation.Range);
		return new(new(source, range), identifier, type, initializer, isMutable)
		{
			IsConstant = isConstant,
			Keyword = keyword
		};
	}
	
	private IExpressionNode ParseExpression(ref int index) => CreateExpressionParser().Parse(ref index);
	
	private ExpressionParser CreateExpressionParser() => new(Tokens, ContextualKeywords)
	{
		ParseBlock = ParseBlockStatement,
		ReportRecovered = Diagnostics.Add,
		ReportInactive = MarkInactive,
		ParseWhenCondition = ParseCondition
	};
	
	private ITypeNode ParseType(ref int index) =>
		new TypeParser(Tokens, ContextualKeywords) { ReportRecovered = Diagnostics.Add }.Parse(ref index);
}