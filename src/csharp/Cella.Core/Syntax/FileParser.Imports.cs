using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private ModuleName? ParseModuleName(ref int index)
	{
		var parts = new List<Token>();
		if (!IsOnSameLine(index) || !Match(ref index, out var firstPart, TokenType.Identifier))
		{
			var isOnSameLine = IsOnSameLine(index);
			ReportExpected(isOnSameLine ? index : index - 1, "a module name");
			if (isOnSameLine)
				ResyncTopLevel(ref index);
			
			return null;
		}
		
		RejectReservedName(firstPart);
		parts.Add(firstPart);
		
		while (IsOnSameLine(index) && Match(ref index, TokenType.OpDot))
		{
			if (IsOnSameLine(index) && Match(ref index, out var part, TokenType.Identifier))
			{
				parts.Add(part);
				continue;
			}
			
			var isOnSameLine = IsOnSameLine(index);
			ReportExpected(isOnSameLine ? index : index - 1, "a name after '.'");
			if (isOnSameLine)
				ResyncTopLevel(ref index);
			
			return null;
		}
		
		return new(parts.ToImmutableArray());
	}
	
	private ImportExpression? ParseImportExpression(ref int index)
	{
		if (!IsOnSameLine(index) || !Match(ref index, out var firstPart, TokenType.Identifier))
		{
			ReportExpected(IsOnSameLine(index) ? index : index - 1, "a module name");
			return null;
		}
		
		var parts = new List<Token> { firstPart };
		IImport? import = null;
		while (import is null && Match(ref index, TokenType.OpDot))
		{
			if (!IsOnSameLine(index))
			{
				ReportExpected(index - 1, "a name, '[' or '*' after '.'");
				return null;
			}
			
			if (Match(ref index, out var part, TokenType.Identifier))
			{
				parts.Add(part);
			}
			else if (Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			{
				if (ParseImportList(ref index, openBracket) is not { } list)
					return null;
				
				import = list;
			}
			else if (Match(ref index, TokenType.OpStar))
			{
				import = FullImport.Instance;
			}
			else
			{
				ReportExpected(index, "a name, '[' or '*'");
				return null;
			}
		}
		
		if (import is null)
		{
			import = new TokenImport(parts[^1]);
			parts.RemoveAt(parts.Count - 1);
		}
		
		if (IsOnSameLine(index))
		{
			ReportExpected(index, "the end of the line");
			return null;
		}
		
		return new(new(parts.ToImmutableArray()), import);
	}
	
	private bool IsOnSameLine(int index) => !AtEnd(index) && Tokens[index].Line == Tokens[index - 1].Line;
	
	private ListImport? ParseImportList(ref int index, Token openBracket)
	{
		var names = new List<Token>();
		while (!Match(ref index, TokenType.OpCloseBracket))
		{
			if (names.Count > 0)
			{
				if (!Match(ref index, out var comma, TokenType.OpComma))
				{
					ReportExpected(index, "',' or ']'", openBracket);
					return null;
				}
				
				if (RejectTrailingComma(index, comma, TokenType.OpCloseBracket))
					continue;
			}
			
			if (!Match(ref index, out var name, TokenType.Identifier))
			{
				ReportExpected(index, "a name");
				return null;
			}
			
			names.Add(name);
		}
		
		if (names.Count == 0)
		{
			ReportExpected(index - 1, "a name");
			return null;
		}
		
		return new ListImport(names.ToImmutableArray());
	}
}