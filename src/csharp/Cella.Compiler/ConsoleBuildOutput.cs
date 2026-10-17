using System.Globalization;
using System.Text;
using Cella.Compiler.Building;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Compiler;

internal sealed class ConsoleBuildOutput(string? rootDirectory, bool showHints) : IBuildOutput
{
	public void WriteLine(string text) => Console.WriteLine(text);
	
	public void WriteError(string text) => Console.Error.WriteLine(text);
	
	public void Report(IEnumerable<Diagnostic> diagnostics) => PrintDiagnostics(diagnostics, rootDirectory, showHints);
	
	private const int TabWidth = 4;
	
	private static void PrintDiagnostics(IEnumerable<Diagnostic> diagnostics, string? rootDirectory, bool showHints)
	{
		var diagnosticsByFile = diagnostics
			.Where(diagnostic => showHints || diagnostic.Severity != DiagnosticSeverity.Hint)
			.GroupBy(static d => d.SourceLocation.Source.FilePath)
			.ToDictionary(static g => g.Key, static g => g
				.OrderBy(static d => d.Line)
				.ThenBy(static d => d.Column)
				.ToArray());
		
		foreach (var (file, set) in diagnosticsByFile.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
		{
			if (!string.IsNullOrEmpty(file))
			{
				var path = rootDirectory is not null ? Path.GetRelativePath(rootDirectory, file) : file;
				
				WriteColored($"\n====== {path} ======{Environment.NewLine}{Environment.NewLine}", ConsoleColor.Blue);
			}
			
			foreach (var diagnostic in set)
				PrintDiagnostic(diagnostic);
		}
	}
	
	private static void PrintDiagnostic(Diagnostic diagnostic)
	{
		var severity = diagnostic.Severity;
		var (source, range) = diagnostic.SourceLocation;
		
		if (!range.IsValid)
		{
			var end = source.GetText(new(0, source.Length)).TrimEnd().Length;
			range = new(end, end);
		}
		
		var (severityColor, severityLabel) = severity switch
		{
			DiagnosticSeverity.Error => (ConsoleColor.Red, "Error"),
			DiagnosticSeverity.Warning => (ConsoleColor.Yellow, "Warning"),
			DiagnosticSeverity.Hint => (ConsoleColor.Green, "Hint"),
			_ => (ConsoleColor.Gray, "Info")
		};
		
		if (diagnostic.SourceLocation == SourceLocation.None)
		{
			WriteColored(severityLabel, severityColor);
			Console.Write(": ");
			WriteColored(diagnostic.Message, severityColor);
			Console.WriteLine();
			Console.WriteLine();
			
			return;
		}
		
		const ConsoleColor suggestionColor = ConsoleColor.Cyan;
		
		var (startLine, startCol) = source.GetLineColumn(range.Start);
		var endLine = source.GetLineColumn(range.End).Line;
		
		var firstDisplayLine = Math.Max(1, startLine - 1);
		var gutterWidth = endLine.ToString().Length;
		
		var lineNumbers = Enumerable.Range(firstDisplayLine, endLine - firstDisplayLine + 1).ToList();
		var rawLines = lineNumbers.Select(n => source.GetText(source.GetLineRange(n)).ToString()).ToList();
		
		var commonLeading = rawLines
			.Where(static l => l.Any(static c => !char.IsWhiteSpace(c)))
			.Select(LeadingVisualWidth)
			.DefaultIfEmpty(0)
			.Min();
		
		var strippedLines = rawLines
			.Select(l => StripLeading(l, commonLeading))
			.Select(static r => r.OvershootSpaces > 0 ? new string(' ', r.OvershootSpaces) + r.Text : r.Text)
			.ToList();
		
		WriteColored($"{severityLabel}", severityColor);
		
		if (lineNumbers.Count == 0)
		{
			Console.Write($" at line {startLine}, column {startCol}: ");
			WriteColored(diagnostic.Message, severityColor);
			Console.WriteLine();
			Console.WriteLine();
			
			return;
		}
		
		Console.WriteLine($" at line {startLine}, column {startCol}");
		for (var i = 0; i < lineNumbers.Count; i++)
		{
			var lineNum = lineNumbers[i];
			var stripped = strippedLines[i];
			var isPreceding = lineNum < startLine;
			
			WriteColored(
				$"{lineNum.ToString().PadLeft(gutterWidth)} │ ",
				isPreceding ? ConsoleColor.DarkGray : severityColor);
			
			if (isPreceding)
			{
				WriteColored(ExpandTabs(stripped), ConsoleColor.DarkGray);
				Console.WriteLine();
				continue;
			}
			
			var rawLine = rawLines[i];
			var hlVisStart = lineNum == startLine
				? Math.Max(0, VisualColumn(rawLine, range.Start - source.GetLineRange(startLine).Start) - commonLeading)
				: LeadingVisualWidth(stripped);
			
			var hlVisEnd = lineNum == endLine
				? Math.Max(0, VisualColumn(rawLine, range.End - source.GetLineRange(endLine).Start) - commonLeading)
				: VisualColumn(stripped, stripped.Length);
			
			var lineVisLen = VisualColumn(stripped, stripped.Length);
			hlVisStart = Math.Clamp(hlVisStart, 0, lineVisLen);
			hlVisEnd = Math.Clamp(hlVisEnd, 0, lineVisLen);
			
			var visualCol = 0;
			for (var c = 0; c < stripped.Length;)
			{
				Rune.DecodeFromUtf16(stripped.AsSpan(c), out var rune, out var consumed);
				var isTab = rune.Value == '\t';
				var charVisWidth = isTab ? TabWidth - visualCol % TabWidth : DisplayWidth(rune);
				var expanded = isTab ? new string(' ', charVisWidth) : stripped.Substring(c, consumed);
				var inHighlight = visualCol >= hlVisStart && visualCol < hlVisEnd;
				
				if (inHighlight)
					WriteColored(expanded, severityColor);
				else
					Console.Write(expanded);
				
				visualCol += charVisWidth;
				c += consumed;
			}
			
			Console.WriteLine();
			
			var arrowCount = Math.Max(0, hlVisEnd - hlVisStart);
			var isLast = lineNum == endLine;
			if (!isLast && arrowCount == 0)
				continue;
			
			var gutter = new string(' ', gutterWidth);
			WriteColored($"{gutter}   ", ConsoleColor.DarkGray);
			Console.Write(new string(' ', hlVisStart));
			
			var arrowLine = arrowCount switch
			{
				> 2 => '╘' + new string('═', arrowCount - 2) + '╛',
				2 => "╘╛",
				_ => "^"
			};
			
			WriteColored(arrowLine, severityColor);
			if (!isLast)
			{
				Console.WriteLine();
				continue;
			}
			
			WriteColored($" {diagnostic.Message}", severityColor);
			Console.WriteLine();
			
			var pipeColumn = hlVisStart + Math.Max(arrowCount, 1) - 1;
			foreach (var hint in diagnostic.Hints)
			{
				WriteColored($"{gutter}   {new string(' ', pipeColumn)}| ", ConsoleColor.DarkGray);
				WriteColored(hint, suggestionColor);
				Console.WriteLine();
			}
			
			Console.WriteLine();
		}
	}
	
	private static int LeadingVisualWidth(string line)
	{
		var col = 0;
		foreach (var ch in line)
		{
			if (ch == ' ')
				col++;
			else if (ch == '\t')
				col += TabWidth - col % TabWidth;
			else
				break;
		}
		
		return col;
	}
	
	private static (string Text, int OvershootSpaces) StripLeading(string line, int targetVisualColumns)
	{
		var col = 0;
		var i = 0;
		while (i < line.Length && col < targetVisualColumns)
		{
			if (line[i] == ' ')
			{
				col++;
				i++;
			}
			else if (line[i] == '\t')
			{
				var tabStop = TabWidth - col % TabWidth;
				if (col + tabStop > targetVisualColumns)
				{
					i++;
					var overshoot = col + tabStop - targetVisualColumns;
					return (line[i..], overshoot);
				}
				
				col += tabStop;
				i++;
			}
			else
				break;
		}
		
		return (line[i..], 0);
	}
	
	private static int VisualColumn(string text, int charIndex)
	{
		var col = 0;
		for (var i = 0; i < charIndex && i < text.Length;)
		{
			Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed);
			col += rune.Value == '\t' ? TabWidth - col % TabWidth : DisplayWidth(rune);
			i += consumed;
		}
		
		return col;
	}
	
	private static int DisplayWidth(Rune rune) => Rune.GetUnicodeCategory(rune) switch
	{
		UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format => 0,
		_ => IsWide(rune.Value) ? 2 : 1
	};
	
	private static bool IsWide(int value) => value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E
		or >= 0x3041 and <= 0x33FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xA000 and <= 0xA4CF
		or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60
		or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1F64F or >= 0x1F900 and <= 0x1F9FF
		or >= 0x20000 and <= 0x3FFFD;
	
	private static string ExpandTabs(string text)
	{
		var sb = new StringBuilder(text.Length);
		var col = 0;
		foreach (var rune in text.EnumerateRunes())
		{
			if (rune.Value == '\t')
			{
				var spaces = TabWidth - col % TabWidth;
				sb.Append(' ', spaces);
				col += spaces;
			}
			else
			{
				sb.Append(rune.ToString());
				col += DisplayWidth(rune);
			}
		}
		
		return sb.ToString();
	}
	
	private static void WriteColored(string text, ConsoleColor color)
	{
		if (Console.IsOutputRedirected)
		{
			Console.Write(text);
			return;
		}
		
		var prev = Console.ForegroundColor;
		Console.ForegroundColor = color;
		Console.Write(text);
		Console.ForegroundColor = prev;
	}
}