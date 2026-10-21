using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public static class ImportEdits
{
	public static TextEdit Create(FileNode file, string module, string name)
	{
		var source = file.SourceLocation.Source;
		var imports = file.Imports
			.Where(static candidate => !candidate.IsPrelude && candidate.SourceLocation.Range.IsValid)
			.ToList();
		
		var merged = imports
			.Where(candidate => !candidate.HasSyntaxErrors && candidate.ModuleName.Text == module)
			.OrderBy(static candidate => candidate.Import is ListImport ? 0 : 1)
			.FirstOrDefault(static candidate => candidate.Import is TokenImport or ListImport);
		
		switch (merged.Import)
		{
			case ListImport { Tokens: [.., var last] } list:
				var following = list.Tokens.FirstOrDefault(token => CompareNames(name, token.Text) < 0);
				return following.Text is null
					? Insert(last.SourceLocation.Range.End, $", {name}")
					: Insert(following.SourceLocation.Range.Start, $"{name}, ");
			
			case TokenImport { Token: var single }:
				string[] names = [single.Text, name];
				Array.Sort(names, CompareNames);
				return new(single.SourceLocation, $"[{string.Join(", ", names)}]");
		}
		
		var newline = source.FindNewline();
		var line = $"use {module}.{name}";
		string[] path = [..module.Split('.'), name];
		var next = imports.FirstOrDefault(candidate => ComparePaths(path, PathOf(candidate)) < 0);
		if (next.Import is not null)
			return Insert(next.SourceLocation.Range.Start, line + newline);
		
		if (imports.Count > 0)
			return Insert(imports.Max(static candidate => candidate.SourceLocation.Range.End), newline + line);
		
		return Insert((file.ImportCondition ?? file.ModuleName.SourceLocation).Range.End, newline + newline + line);
		
		TextEdit Insert(int offset, string text) => new(new(source, new(offset, offset)), text);
	}
	
	private static string[] PathOf(ImportExpression import) =>
	[
		..import.ModuleName.Parts.Select(static part => part.Text),
		import.Import switch
		{
			TokenImport single => single.Token.Text,
			ListImport { Tokens: [var first, ..] } => first.Text,
			_ => "*"
		}
	];
	
	private static int ComparePaths(string[] left, string[] right)
	{
		for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
		{
			if (CompareNames(left[i], right[i]) is not 0 and var result)
				return result;
		}
		
		return left.Length.CompareTo(right.Length);
	}
	
	private static int CompareNames(string left, string right) =>
		StringComparer.OrdinalIgnoreCase.Compare(left, right) is not 0 and var result
			? result
			: string.CompareOrdinal(left, right);
}