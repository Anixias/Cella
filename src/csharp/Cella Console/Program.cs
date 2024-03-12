using Cella.Analysis.Syntax;
using Cella.Analysis.Text;
using Cella.Diagnostics;

namespace CellaConsole;

public static class Program
{
	private static readonly object ReportLock = new();
	private static readonly object ConsoleLock = new();
	
	public static int Main(string[] args)
	{
		// Todo: Parse args using OptionSet

		var diagnostics = new DiagnosticList();
		var source = new StringBuffer("mod2 helloWorld\n\nmain: entry(args: String[]): !{io} Int32\n{\n\tret 0\n}");
		var lexer = new FilteredLexer(source);
		var (ast, parserDiagnostics) = Parser.Parse(lexer);
		diagnostics.Add(parserDiagnostics);
		
		if (ast is null)
		{
			PrintDiagnostics(diagnostics);
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine("Failed to compile");
			return 1;
		}

		AstPrinter.Print(ast, Console.Out);
		PrintDiagnostics(diagnostics);
		return 0;
	}

	private static void PrintDiagnostics(DiagnosticList diagnostics)
	{
		if (diagnostics.Count <= 0)
			return;

		lock (ReportLock)
		{
			PrintDiagnosticsOfSeverity(diagnostics, DiagnosticSeverity.Warning);
			PrintDiagnosticsOfSeverity(diagnostics, DiagnosticSeverity.Error);
		}
	}

	private static void PrintDiagnosticsOfSeverity(DiagnosticList diagnostics, DiagnosticSeverity severity)
	{
		diagnostics = diagnostics.OfSeverity(severity);
		if (diagnostics.Count <= 0)
			return;
		
		var color = severity switch
		{
			DiagnosticSeverity.Error => ConsoleColor.Red,
			DiagnosticSeverity.Warning => ConsoleColor.Yellow,
			_ => ConsoleColor.White
		};
			
		var darkColor = severity switch
		{
			DiagnosticSeverity.Error => ConsoleColor.DarkRed,
			DiagnosticSeverity.Warning => ConsoleColor.DarkYellow,
			_ => ConsoleColor.White
		};

		foreach (var diagnostic in diagnostics.OrderBy(d => d.line))
		{
			const int maxLineNumberLength = 8;
			const char lineBar = '\u2502';
			const char upArrow = '\u2191';
			const char downArrow = '\u2193';
			
			// Todo: Support multiline errors
			
			var lineHeader = diagnostic.line == 0
				? $"{"?",maxLineNumberLength}"
				: $"{diagnostic.line.ToString(),maxLineNumberLength}";

			var messageHeader = $"{new string(' ', lineHeader.Length)} {lineBar} ";
			var lineRange = diagnostic.source.GetLineRange(diagnostic.line);
			var lineText = diagnostic.source.GetText(diagnostic.line);
			lock (ConsoleLock)
			{
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write($"{lineHeader} {lineBar} ");
				Console.ForegroundColor = ConsoleColor.Gray;
				
				if (diagnostic.range is { } range)
				{
					var columnSkips = 0;
					var preRange = new TextRange(lineRange.Start, range.Start);
					while (char.IsWhiteSpace(diagnostic.source[preRange.Start]))
					{
						preRange = new TextRange(preRange.Start + 1, preRange.End);
						columnSkips++;
					}
					
					var postRange = new TextRange(range.End, lineRange.End);

					if (diagnostic.line > 0)
					{
						Console.Write(diagnostic.source.GetText(preRange));
						Console.ForegroundColor = darkColor;
						Console.Write(diagnostic.source.GetText(range));
						Console.ForegroundColor = ConsoleColor.Gray;
						Console.WriteLine(diagnostic.source.GetText(postRange));
					}
					else
					{
						Console.WriteLine();
					}

					Console.ForegroundColor = ConsoleColor.DarkGray;
					Console.Write(messageHeader);
					Console.ForegroundColor = darkColor;
					var column = diagnostic.source.GetLineColumn(range.Start).Item2 - (columnSkips + 1);
					Console.Write(new string(' ', column));
					Console.WriteLine(new string(upArrow, range.Length));
				}
				else
				{
					if (diagnostic.line > 0)
						Console.WriteLine(lineText);
					else
						Console.WriteLine();
				}

				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write(messageHeader);
				Console.ForegroundColor = color;
				Console.WriteLine(diagnostic.message);
				
				Console.ResetColor();
				Console.WriteLine();
			}
		}
	}
}