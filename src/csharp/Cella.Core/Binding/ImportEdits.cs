using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public static class ImportEdits
{
	public const string Wildcard = "*";
	
	public static TextEdit Create(FileNode file, string module, string name) => Create(file, module, [name]).Single();
	
	public static IEnumerable<TextEdit> Create(FileNode file, string module, IReadOnlyCollection<string> names)
	{
		var source = file.SourceLocation.Source;
		var imports = file.Imports
			.Where(static candidate => !candidate.IsPrelude && candidate.SourceLocation.Range.IsValid)
			.ToList();
		
		List<string> sorted = names.Contains(Wildcard) ? [Wildcard] : [..names.Distinct().Order(Comparer)];
		var merged = sorted is [Wildcard]
			? default
			: imports
				.Where(candidate => !candidate.HasSyntaxErrors && candidate.ModuleName.Text == module)
				.OrderBy(static candidate => candidate.Import is ListImport ? 0 : 1)
				.FirstOrDefault(static candidate => candidate.Import is TokenImport or ListImport);
		
		switch (merged.Import)
		{
			case ListImport { Tokens: [.., var last] } list:
				foreach (var group in sorted.GroupBy(name =>
					         list.Tokens.IndexOf(
						         list.Tokens.FirstOrDefault(token => CompareNames(name, token.Text) < 0))))
				{
					yield return group.Key < 0
						? Insert(last.SourceLocation.Range.End, string.Concat(group.Select(static name => $", {name}")))
						: Insert(list.Tokens[group.Key].SourceLocation.Range.Start,
							string.Concat(group.Select(static name => $"{name}, ")));
				}
				
				yield break;
			
			case TokenImport { Token: var single }:
				yield return new(single.SourceLocation,
					$"[{string.Join(", ", sorted.Append(single.Text).Order(Comparer))}]");
				
				yield break;
		}
		
		var newline = source.FindNewline();
		var line = $"use {module}.{(sorted is [var only] ? only : $"[{string.Join(", ", sorted)}]")}";
		var next = imports.FirstOrDefault(candidate =>
			CompareImports(module, sorted[0], candidate.ModuleName.Text, FirstNameOf(candidate)) < 0);
		
		if (next.Import is not null)
			yield return Insert(next.SourceLocation.Range.Start, line + newline);
		else if (imports.Count > 0)
			yield return Insert(imports.Max(static candidate => candidate.SourceLocation.Range.End), newline + line);
		else
			yield return Insert((file.ImportCondition ?? file.ModuleName.SourceLocation).Range.End,
				newline + newline + line);
		
		TextEdit Insert(int offset, string text) => new(new(source, new(offset, offset)), text);
	}
	
	public static IComparer<string> Comparer { get; } = Comparer<string>.Create(CompareNames);
	
	public static int CompareModules(string left, string right)
	{
		var leftParts = left.Split('.');
		var rightParts = right.Split('.');
		for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
		{
			if (CompareNames(leftParts[i], rightParts[i]) is not 0 and var result)
				return result;
		}
		
		return leftParts.Length.CompareTo(rightParts.Length);
	}
	
	private static int CompareImports(string leftModule, string leftName, string rightModule, string rightName) =>
		CompareModules(leftModule, rightModule) is not 0 and var result ? result : CompareNames(leftName, rightName);
	
	private static string FirstNameOf(ImportExpression import) => import.Import switch
	{
		TokenImport single => single.Token.Text,
		ListImport { Tokens: [var first, ..] } => first.Text,
		_ => Wildcard
	};
	
	private static int CompareNames(string left, string right) =>
		StringComparer.OrdinalIgnoreCase.Compare(left, right) is not 0 and var result
			? result
			: string.CompareOrdinal(left, right);
}