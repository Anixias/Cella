using System.Text;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.LanguageServer;

public static class MatchConversions
{
	private const string IfKeyword = "if";
	private const string MatchKeyword = "match";
	
	private sealed record Arm(string Head, IStatementNode Body, IReadOnlyList<string> Comments);
	
	private sealed record Branch(string? Condition, IStatementNode Body, IReadOnlyList<string> Comments);
	
	public static IEnumerable<DiagnosticFix> Find(IEnumerable<FunctionNode> functions, TextRange requested)
	{
		var fixes = new List<DiagnosticFix>();
		foreach (var function in functions)
		{
			if (function.Body is { } body)
				VisitBody(body, requested, fixes);
		}
		
		return fixes;
	}
	
	private static void VisitBody(IStatementNode body, TextRange requested, List<DiagnosticFix> fixes)
	{
		if (body is BlockStatementNode block)
			VisitList(block.StatementNodes, requested, fixes);
		else
			VisitList([body], requested, fixes);
	}
	
	private static void VisitList(IReadOnlyList<IStatementNode> statements, TextRange requested,
		List<DiagnosticFix> fixes)
	{
		for (var i = 0; i < statements.Count; i++)
		{
			if (Consider(statements, i, requested) is { } fix)
				fixes.Add(fix);
			
			VisitChildren(statements[i], requested, fixes);
		}
	}
	
	private static void VisitChildren(IStatementNode statement, TextRange requested, List<DiagnosticFix> fixes)
	{
		switch (statement)
		{
			case BlockStatementNode block:
				VisitList(block.StatementNodes, requested, fixes);
				break;
			
			case IfStatementNode branch:
				VisitBody(branch.Then, requested, fixes);
				if (branch.Else is IfStatementNode next)
					VisitChildren(next, requested, fixes);
				else if (branch.Else is { } otherwise)
					VisitBody(otherwise, requested, fixes);
				
				break;
			
			case WhileStatementNode loop:
				VisitBody(loop.Body, requested, fixes);
				break;
			
			case ForStatementNode loop:
				VisitBody(loop.Body, requested, fixes);
				break;
			
			case LoopStatementNode loop:
				VisitBody(loop.Body, requested, fixes);
				break;
			
			case DoWhileStatementNode loop:
				VisitBody(loop.Body, requested, fixes);
				break;
			
			case MatchStatementNode match:
				foreach (var arm in match.Arms)
					VisitBody(arm.Body, requested, fixes);
				
				break;
		}
	}
	
	private static DiagnosticFix? Consider(IReadOnlyList<IStatementNode> statements, int index, TextRange requested)
	{
		switch (statements[index])
		{
			case IfStatementNode { Else: not null } chain when TouchesChain(chain, requested):
				return ToMatch(chain);
			
			case IfStatementNode { Else: null } single when TouchesKeyword(single.SourceLocation, IfKeyword, requested):
				return ToMatch(statements, index);
			
			case MatchStatementNode match when TouchesKeyword(match.SourceLocation, MatchKeyword, requested):
				return ToIf(match, index == statements.Count - 1);
			
			default:
				return null;
		}
	}
	
	private static bool TouchesChain(IfStatementNode chain, TextRange requested)
	{
		for (IStatementNode? node = chain; node is IfStatementNode branch; node = branch.Else)
		{
			if (TouchesKeyword(branch.SourceLocation, IfKeyword, requested))
				return true;
		}
		
		return false;
	}
	
	private static bool TouchesKeyword(SourceLocation location, string keyword, TextRange requested)
	{
		var start = location.Range.Start;
		return start + keyword.Length <= location.Source.Length &&
		       location.Source.GetText(new(start, start + keyword.Length)).ToString() == keyword &&
		       start <= requested.End && requested.Start <= start + keyword.Length;
	}
	
	private static DiagnosticFix? ToMatch(IfStatementNode chain)
	{
		var source = chain.SourceLocation.Source;
		var branches = new List<(IExpressionNode Condition, IStatementNode Body)>();
		IStatementNode? otherwise = null;
		var node = chain;
		while (true)
		{
			branches.Add((node.Condition, node.Then));
			if (node.Else is null)
				break;
			
			if (!IsElseGap(source, node.Then.SourceLocation.Range.End, node.Else.SourceLocation.Range.Start))
				return null;
			
			if (node.Else is not IfStatementNode next)
			{
				otherwise = node.Else;
				break;
			}
			
			node = next;
		}
		
		if (branches.Count < 2 || FindSubject(branches.Select(static branch => branch.Condition).ToList()) is not
			    { } subject)
			return null;
		
		var arms = branches.Select(branch => new Arm(ToArmHead(branch.Condition, subject.Text)!, branch.Body, []))
			.ToList();
		
		if (otherwise is not null)
			arms.Add(new("else", otherwise, []));
		
		var end = (otherwise ?? branches[^1].Body).SourceLocation.Range.End;
		return CreateMatch(source, chain.SourceLocation.Range.Start, end, subject.Node, arms);
	}
	
	private static DiagnosticFix? ToMatch(IReadOnlyList<IStatementNode> statements, int index)
	{
		var source = statements[index].SourceLocation.Source;
		if (statements[index] is not IfStatementNode current)
			return null;
		
		IExpressionNode? best = null;
		var (first, last) = (index, index);
		foreach (var candidate in FindSubjectCandidates(current.Condition))
		{
			var text = Normalize(candidate);
			var start = index;
			while (start > 0 && IsGuardBranch(statements[start - 1], text) && ExitsBranch(statements[start - 1]) &&
			       FindComments(source, statements[start - 1].SourceLocation.Range.End,
				       statements[start].SourceLocation.Range.Start) is not null)
				start--;
			
			var end = index;
			while (end + 1 < statements.Count && ExitsBranch(statements[end]) &&
			       IsGuardBranch(statements[end + 1], text) &&
			       FindComments(source, statements[end].SourceLocation.Range.End,
				       statements[end + 1].SourceLocation.Range.Start) is not null)
				end++;
			
			if (start <= index && index <= end && end - start > last - first)
				(best, first, last) = (candidate, start, end);
		}
		
		if (best is null || last == first)
			return null;
		
		var subject = Normalize(best);
		var arms = new List<Arm>();
		for (var i = first; i <= last; i++)
		{
			var comments = i == first
				? []
				: FindComments(source, statements[i - 1].SourceLocation.Range.End,
					statements[i].SourceLocation.Range.Start)!;
			
			var branch = (IfStatementNode)statements[i];
			arms.Add(new(ToArmHead(branch.Condition, subject)!, branch.Then, comments));
		}
		
		var replaced = statements[last].SourceLocation.Range.End;
		if (last + 2 == statements.Count && ExitsBranch(statements[last]) &&
		    statements[last + 1] is not VarStatementNode &&
		    FindComments(source, replaced, statements[last + 1].SourceLocation.Range.Start) is { } restComments)
		{
			arms.Add(new("else", statements[last + 1], restComments));
			replaced = statements[last + 1].SourceLocation.Range.End;
		}
		
		return CreateMatch(source, statements[first].SourceLocation.Range.Start, replaced, best, arms);
	}
	
	private static bool ExitsBranch(IStatementNode statement) =>
		statement is IfStatementNode { Else: null } branch && Diverges(branch.Then);
	
	private static bool IsGuardBranch(IStatementNode statement, string subject) =>
		statement is IfStatementNode { Else: null } branch && ToArmHead(branch.Condition, subject) is not null;
	
	private static (IExpressionNode Node, string Text)? FindSubject(List<IExpressionNode> conditions)
	{
		foreach (var candidate in FindSubjectCandidates(conditions[0]))
		{
			var text = Normalize(candidate);
			if (conditions.All(condition => ToArmHead(condition, text) is not null))
				return (candidate, text);
		}
		
		return null;
	}
	
	private static IEnumerable<IExpressionNode> FindSubjectCandidates(IExpressionNode condition)
	{
		switch (condition)
		{
			case BinaryOpExpressionNode { Op.Type: TokenType.OpAmpersandAmpersand or TokenType.OpBarBar } logical:
				foreach (var candidate in FindSubjectCandidates(logical.Left))
					yield return candidate;
				
				break;
			
			case BinaryOpExpressionNode comparison when IsComparison(comparison.Op.Type):
				if (IsStable(comparison.Left))
					yield return comparison.Left;
				
				if (IsStable(comparison.Right))
					yield return comparison.Right;
				
				break;
			
			case IsExpressionNode { Mode: null } test when IsStable(test.Value):
				yield return test.Value;
				break;
		}
	}
	
	private static bool IsStable(IExpressionNode node) => node switch
	{
		VarExpressionNode => true,
		AccessExpressionNode access => IsStable(access.Target),
		_ => false
	};
	
	private static string? ToArmHead(IExpressionNode condition, string subject)
	{
		if (condition is BinaryOpExpressionNode { Op.Type: TokenType.OpBarBar })
		{
			var patterns = Flatten(condition, TokenType.OpBarBar).Select(item => ToPatternItem(item, subject))
				.ToList();
			
			return patterns.Any(static pattern => pattern is null) ? null : string.Join(", ", patterns);
		}
		
		var conjuncts = Flatten(condition, TokenType.OpAmpersandAmpersand);
		var (pattern, used) = conjuncts.Count >= 2 && ToRange(conjuncts[0], conjuncts[1], subject) is { } range
			? (range, 2)
			: (ToPattern(conjuncts[0], subject), 1);
		
		if (pattern is null)
			return null;
		
		return used == conjuncts.Count
			? pattern
			: $"{pattern} if {string.Join(" && ", conjuncts.Skip(used).Select(static guard => Wrap(guard, 1)))}";
	}
	
	private static string? ToPatternItem(IExpressionNode item, string subject) =>
		Flatten(item, TokenType.OpAmpersandAmpersand) switch
		{
			[var only] => ToPattern(only, subject),
			[var lower, var upper] => ToRange(lower, upper, subject),
			_ => null
		};
	
	private static string? ToPattern(IExpressionNode condition, string subject)
	{
		switch (condition)
		{
			case IsExpressionNode { Mode: null } test when Normalize(test.Value) == subject:
				return Text(test.Pattern.SourceLocation);
			
			case BinaryOpExpressionNode when Bound(condition, subject) is var (op, value):
				return op == TokenType.OpEqualEqual ? Wrap(value, 5) : $"{OperatorText(op)} {Wrap(value, 5)}";
			
			default:
				return null;
		}
	}
	
	private static string? ToRange(IExpressionNode lower, IExpressionNode upper, string subject)
	{
		if (Bound(lower, subject) is not (TokenType.OpGreaterEqual, var start) ||
		    Bound(upper, subject) is not (TokenType.OpLess or TokenType.OpLessEqual, var end) bound)
			return null;
		
		var separator = bound.Op == TokenType.OpLess ? ".." : "..=";
		return $"{Wrap(start, 6)}{separator}{Wrap(end, 6)}";
	}
	
	private static (TokenType Op, IExpressionNode Value)? Bound(IExpressionNode condition, string subject) =>
		condition switch
		{
			BinaryOpExpressionNode comparison when IsComparison(comparison.Op.Type) &&
			                                       Normalize(comparison.Left) == subject &&
			                                       IsStable(comparison.Left) =>
				(comparison.Op.Type, comparison.Right),
			BinaryOpExpressionNode comparison when IsComparison(comparison.Op.Type) &&
			                                       Normalize(comparison.Right) == subject &&
			                                       IsStable(comparison.Right) =>
				(Flip(comparison.Op.Type), comparison.Left),
			_ => null
		};
	
	private static List<IExpressionNode> Flatten(IExpressionNode node, TokenType op)
	{
		if (node is not BinaryOpExpressionNode binary || binary.Op.Type != op)
			return [node];
		
		return [..Flatten(binary.Left, op), ..Flatten(binary.Right, op)];
	}
	
	private static bool IsComparison(TokenType type) => type is TokenType.OpEqualEqual or TokenType.OpBangEqual or
		TokenType.OpLess or TokenType.OpLessEqual or TokenType.OpGreater or TokenType.OpGreaterEqual;
	
	private static TokenType Flip(TokenType type) => type switch
	{
		TokenType.OpLess => TokenType.OpGreater,
		TokenType.OpLessEqual => TokenType.OpGreaterEqual,
		TokenType.OpGreater => TokenType.OpLess,
		TokenType.OpGreaterEqual => TokenType.OpLessEqual,
		_ => type
	};
	
	private static string OperatorText(TokenType type) => type switch
	{
		TokenType.OpEqualEqual => "==",
		TokenType.OpBangEqual => "!=",
		TokenType.OpLess => "<",
		TokenType.OpLessEqual => "<=",
		TokenType.OpGreater => ">",
		_ => ">="
	};
	
	private static int LevelOf(IExpressionNode node) => node switch
	{
		BinaryOpExpressionNode binary => binary.Op.Type switch
		{
			TokenType.OpBarBar => 1,
			TokenType.OpAmpersandAmpersand => 2,
			TokenType.OpEqualEqual or TokenType.OpBangEqual => 3,
			TokenType.OpLess or TokenType.OpLessEqual or TokenType.OpGreater or TokenType.OpGreaterEqual => 5,
			TokenType.OpBar or TokenType.OpHat or TokenType.OpAmpersand or TokenType.OpLessLess or
				TokenType.OpGreaterGreater or TokenType.OpPlus or TokenType.OpMinus or TokenType.OpStar or
				TokenType.OpSlash or TokenType.OpPercent or TokenType.OpPlusPercent or TokenType.OpMinusPercent or
				TokenType.OpStarPercent => 6,
			_ => 0
		},
		IsExpressionNode => 4,
		LambdaExpressionNode => 0,
		_ => 7
	};
	
	private static string Wrap(IExpressionNode node, int level)
	{
		var text = Text(node.SourceLocation);
		return LevelOf(node) <= level ? $"({text})" : text;
	}
	
	private static string Text(SourceLocation location) => location.GetText().ToString();
	
	private static string Normalize(IExpressionNode node) =>
		string.Concat(Text(node.SourceLocation).Where(static c => !char.IsWhiteSpace(c)));
	
	private static bool Diverges(IStatementNode statement) => statement switch
	{
		BlockStatementNode block => block.StatementNodes is [.., var last] && Diverges(last),
		ExpressionStatementNode
		{
			ExpressionNode: ReturnExpressionNode or BreakExpressionNode or ContinueExpressionNode or
			PanicExpressionNode
		} => true,
		IfStatementNode { Then: var then, Else: { } otherwise } => Diverges(then) && Diverges(otherwise),
		_ => false
	};
	
	private static bool IsElseGap(ISource source, int start, int end) =>
		source.GetText(new(start, end)).ToString().Trim() == "else";
	
	private static List<string>? FindComments(ISource source, int start, int end)
	{
		var comments = new List<string>();
		foreach (var line in source.GetText(new(start, end)).ToString().Split('\n'))
		{
			var trimmed = line.Trim().Trim(',', '{', '}').Trim();
			if (trimmed.Length == 0)
				continue;
			
			if (!trimmed.StartsWith("//", StringComparison.Ordinal))
				return null;
			
			comments.Add(trimmed);
		}
		
		return comments;
	}
	
	private static DiagnosticFix CreateMatch(ISource source, int start, int end, IExpressionNode subject,
		List<Arm> arms)
	{
		var newline = source.FindNewline();
		var indent = source.FindIndent(start);
		var armIndent = indent + "\t";
		var text = new StringBuilder($"match {Text(subject.SourceLocation)}{newline}{indent}{{");
		foreach (var arm in arms)
		{
			foreach (var comment in arm.Comments)
				text.Append(newline).Append(armIndent).Append(comment);
			
			text.Append(newline).Append(armIndent).Append(RenderArm(source, arm, armIndent, newline));
		}
		
		text.Append(newline).Append(indent).Append('}');
		return DiagnosticFix.Replace("Convert to 'match'", new(source, new(start, end)), text.ToString());
	}
	
	private static string RenderArm(ISource source, Arm arm, string armIndent, string newline)
	{
		if (arm.Body is BlockStatementNode block)
		{
			if (block.StatementNodes is [ExpressionStatementNode only] && IsBare(source, block, only))
				return $"{arm.Head} => {Reindent(source, only.SourceLocation, armIndent)}";
			
			return $"{arm.Head} =>{newline}{armIndent}{Reindent(source, block.SourceLocation, armIndent)}";
		}
		
		return $"{arm.Head} => {Reindent(source, arm.Body.SourceLocation, armIndent)}";
	}
	
	private static bool IsBare(ISource source, BlockStatementNode block, IStatementNode only)
	{
		var range = block.SourceLocation.Range;
		return source.GetText(new(range.Start + 1, range.End - 1)).ToString().Trim() ==
		       Text(only.SourceLocation).Trim();
	}
	
	private static string Reindent(ISource source, SourceLocation location, string indent) =>
		Refactorings.Reindent(Text(location), source.FindIndent(location.Range.Start), indent);
	
	private static DiagnosticFix? ToIf(MatchStatementNode match, bool endsBlock)
	{
		if (match.Mode is not null || !IsStable(match.Value) || match.Arms.IsEmpty)
			return null;
		
		var source = match.SourceLocation.Source;
		var subject = Text(match.Value.SourceLocation);
		var branches = new List<Branch>();
		var position = match.Value.SourceLocation.Range.End;
		foreach (var arm in match.Arms)
		{
			if (FindComments(source, position, arm.SourceLocation.Range.Start) is not { } comments)
				return null;
			
			if (arm.Patterns.IsEmpty)
				branches.Add(new(null, arm.Body, comments));
			else if (ToCondition(arm, subject) is { } condition)
				branches.Add(new(condition, arm.Body, comments));
			else
				return null;
			
			position = arm.Body.SourceLocation.Range.End;
		}
		
		if (FindComments(source, position, match.SourceLocation.Range.End) is not { Count: 0 })
			return null;
		
		var otherwise = branches.Where(static branch => branch.Condition is null).ToList();
		if (otherwise.Count > 1 || otherwise is [var last] && last != branches[^1] ||
		    branches.All(static branch => branch.Condition is null))
			return null;
		
		var newline = source.FindNewline();
		var indent = source.FindIndent(match.SourceLocation.Range.Start);
		var conditional = branches.Where(static branch => branch.Condition is not null).ToList();
		var text = new StringBuilder();
		if (conditional.All(static branch => Diverges(branch.Body)))
		{
			var separator = newline + indent + newline + indent;
			for (var i = 0; i < conditional.Count; i++)
			{
				if (i > 0)
					text.Append(separator);
				
				AppendComments(text, conditional[i].Comments, indent, newline);
				text.Append($"if {conditional[i].Condition}").Append(RenderBranch(source, conditional[i].Body, indent,
					newline));
			}
			
			if (otherwise is [var rest] && RenderRest(source, rest.Body, indent, endsBlock) is { Length: > 0 } restText)
			{
				text.Append(separator);
				AppendComments(text, rest.Comments, indent, newline);
				text.Append(restText);
			}
		}
		else
		{
			for (var i = 0; i < branches.Count; i++)
			{
				if (i > 0)
					text.Append(newline).Append(indent).Append("else");
				
				if (branches[i].Comments.Count > 0)
					return null;
				
				if (branches[i].Condition is { } condition)
					text.Append(i > 0 ? " " : "").Append($"if {condition}");
				
				text.Append(RenderBranch(source, branches[i].Body, indent, newline));
			}
		}
		
		return DiagnosticFix.Replace("Convert to 'if'", match.SourceLocation, text.ToString());
	}
	
	private static void AppendComments(StringBuilder text, IReadOnlyList<string> comments, string indent,
		string newline)
	{
		foreach (var comment in comments)
			text.Append(comment).Append(newline).Append(indent);
	}
	
	private static string RenderBranch(ISource source, IStatementNode body, string indent, string newline) =>
		body is BlockStatementNode
			? $"{newline}{indent}{Reindent(source, body.SourceLocation, indent)}"
			: $"{newline}{indent}\t{Reindent(source, body.SourceLocation, indent + "\t")}";
	
	private static string RenderRest(ISource source, IStatementNode body, string indent, bool endsBlock)
	{
		if (body is not BlockStatementNode block)
			return Reindent(source, body.SourceLocation, indent);
		
		if (!endsBlock || block.StatementNodes.IsEmpty)
			return block.StatementNodes.IsEmpty ? "" : Reindent(source, block.SourceLocation, indent);
		
		var range = block.SourceLocation.Range;
		var inner = source.GetText(new(range.Start + 1, range.End - 1)).ToString().Trim();
		return Refactorings.Reindent(inner, source.FindIndent(block.StatementNodes[0].SourceLocation.Range.Start),
			indent);
	}
	
	private static string? ToCondition(MatchArmNode arm, string subject)
	{
		var items = arm.Patterns.Select(pattern => ToTest(pattern, subject)).ToList();
		if (items.Any(static item => item is null))
			return null;
		
		var condition = string.Join(" || ", items);
		if (arm.Guard is not { } guard)
			return condition;
		
		return $"{(items.Count > 1 ? $"({condition})" : condition)} && {Wrap(guard, 1)}";
	}
	
	private static string? ToTest(PatternNode pattern, string subject)
	{
		switch (pattern.Kind)
		{
			case PatternKind.Value when pattern.Value is { } value:
				return $"{subject} == {Wrap(value, 3)}";
			
			case PatternKind.Relational when pattern is { Value: { } value, Operator: { } op }:
				return $"{subject} {op.Text} {Wrap(value, op.Type is TokenType.OpBangEqual ? 3 : 5)}";
			
			case PatternKind.Range when pattern is { Value: { } lower, Upper: { } upper }:
				var upperOp = pattern.IsInclusive ? "<=" : "<";
				return $"{Wrap(lower, 5)} <= {subject} && {subject} {upperOp} {Wrap(upper, 5)}";
			
			default:
				return $"{subject} is {Text(pattern.SourceLocation)}";
		}
	}
}