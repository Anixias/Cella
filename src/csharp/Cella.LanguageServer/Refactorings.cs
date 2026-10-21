using Cella.Core.Binding.Nodes;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.LanguageServer;

public static class Refactorings
{
	public static IEnumerable<DiagnosticFix> Find(ResolvedFileNode file, TextRange requested)
	{
		if (file.Syntax is not FileNode syntax)
			yield break;
		
		foreach (var function in FindFunctions(syntax.Declarations))
		{
			if (function.Body is null || !Touches(Header(function), requested))
				continue;
			
			if (function.BodyArrow is { } arrow && ToBlockBody(function, arrow) is { } block)
				yield return block;
			else if (function.Body is BlockStatementNode body && ToExpressionBody(function, body) is { } expression)
				yield return expression;
		}
	}
	
	private static IEnumerable<FunctionNode> FindFunctions(IEnumerable<IDeclarationNode> declarations)
	{
		foreach (var declaration in declarations)
		{
			var nested = declaration switch
			{
				FunctionNode function => [function],
				PropertyNode property => property.Accessors,
				RecordNode record => FindFunctions(record.Members),
				TraitNode trait => FindFunctions(trait.Members),
				ImplNode impl => FindFunctions(impl.Members),
				EnumNode enumNode => FindFunctions(enumNode.Members),
				_ => []
			};
			
			foreach (var function in nested)
				yield return function;
		}
	}
	
	private static TextRange Header(FunctionNode function)
	{
		var end = function.BodyArrow?.SourceLocation.Range.End ?? function.Body!.SourceLocation.Range.Start;
		return new(function.Identifier.SourceLocation.Range.Start, end);
	}
	
	private static DiagnosticFix? ToBlockBody(FunctionNode function, Token arrow)
	{
		var statement = function.Body switch
		{
			ExpressionStatementNode { ExpressionNode: ReturnExpressionNode { Value: { } value } returned }
				when returned.SourceLocation == value.SourceLocation =>
				(Location: value.SourceLocation, Prefix: "ret "),
			ExpressionStatementNode expression => (expression.SourceLocation, ""),
			MatchStatementNode match => (match.SourceLocation, ""),
			_ => default((SourceLocation Location, string Prefix)?)
		};
		
		if (statement is not var (location, prefix))
			return null;
		
		var source = location.Source;
		var indent = source.FindIndent(function.Identifier.SourceLocation.Range.Start);
		var isOnHeaderLine = LineOf(source, location.Range.Start) == LineOf(source, arrow.SourceLocation.Range.Start);
		var oldIndent = isOnHeaderLine ? indent : source.FindIndent(location.Range.Start);
		var text = Reindent(location.GetText().ToString(), oldIndent, indent + "\t");
		var start = SkipSpacesBackward(source, arrow.SourceLocation.Range.Start);
		var newline = source.FindNewline();
		return DiagnosticFix.Replace("Convert to block body", new(source, new(start, location.Range.End)),
			$"{newline}{indent}{{{newline}{indent}\t{prefix}{text}{newline}{indent}}}");
	}
	
	private static DiagnosticFix? ToExpressionBody(FunctionNode function, BlockStatementNode body)
	{
		if (body.StatementNodes is not [var only])
			return null;
		
		var location = only switch
		{
			ExpressionStatementNode { ExpressionNode: ReturnExpressionNode { Value: { } value } } when
				function.ReturnType is not null => value.SourceLocation,
			ExpressionStatementNode { ExpressionNode: not ReturnExpressionNode } statement when
				function.ReturnType is null => statement.SourceLocation,
			MatchStatementNode match when function.ReturnType is null => match.SourceLocation,
			_ => default(SourceLocation?)
		};
		
		if (location is not { } expression)
			return null;
		
		var source = expression.Source;
		var indent = source.FindIndent(expression.Range.Start);
		var text = Reindent(expression.GetText().ToString(), indent,
			source.FindIndent(body.SourceLocation.Range.Start));
		
		var start = SkipSpacesBackward(source, body.SourceLocation.Range.Start);
		return DiagnosticFix.Replace("Convert to expression body",
			new(source, new(start, body.SourceLocation.Range.End)),
			$" => {text}");
	}
	
	private static string Reindent(string text, string oldIndent, string newIndent)
	{
		var lines = text.Split('\n');
		for (var i = 1; i < lines.Length; i++)
		{
			if (lines[i].StartsWith(oldIndent, StringComparison.Ordinal))
				lines[i] = newIndent + lines[i][oldIndent.Length..];
		}
		
		return string.Join('\n', lines);
	}
	
	private static int LineOf(ISource source, int position) => source.GetLineColumn(position).Line;
	
	private static int SkipSpacesBackward(ISource source, int position)
	{
		while (position > 0 && char.IsWhiteSpace(source[position - 1]))
			position--;
		
		return position;
	}
	
	private static bool Touches(TextRange range, TextRange requested) =>
		range.Start <= requested.End && requested.Start <= range.End;
}