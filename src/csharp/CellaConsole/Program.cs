using Cella.Analysis.Syntax;
using Cella.Analysis.Text;
using Cella.Diagnostics;

namespace CellaConsole;

public static class Program
{
	private static readonly string[] NewlineSeparators = ["\r\n", "\n", "\r"];
	private static readonly object ReportLock = new();
	private static readonly object ConsoleLock = new();
	
	public static int Main(string[] args)
	{
		if (Execute(args, out var elapsedTime))
		{
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine($"Compilation succeeded ({Format(elapsedTime)})");
			return 0;
		}

		Console.ForegroundColor = ConsoleColor.Red;
		Console.WriteLine("Failed to compile");
		return 1;
	}

	private static string Format(TimeSpan time)
	{
		if (time < TimeSpan.FromMilliseconds(1.0))
		{
			return $"{time.TotalNanoseconds} ns";
		}
		
		if (time < TimeSpan.FromSeconds(1.0))
		{
			return $"{time.TotalMilliseconds} ms";
		}
		
		if (time < TimeSpan.FromMinutes(1.0))
		{
			return $"{time.TotalSeconds} s";
		}
		
		if (time < TimeSpan.FromHours(1.0))
		{
			return $"{time.TotalMinutes} min";
		}
		
		return $"{time.TotalHours} h";
	}

	private static bool Execute(string[] args, out TimeSpan elapsedTime)
	{
		// Todo: Parse args using OptionSet

		var startTime = DateTime.UtcNow;
		var result = TestHelloWorld();
		var endTime = DateTime.UtcNow;

		elapsedTime = endTime - startTime;
		return result;
	}

	private static bool TestHelloWorld()
	{
		var result = CompileSource(new CompilationSource.RawText("mod helloWorld\n\n" +
		                                                         "main: entry(args: String[]): Int32\n" +
		                                                         "{\n\tret 123\n}"));

		result.Wait();
		return result.Result.IsSuccess;
	}

	private static void PrintDiagnostics(DiagnosticList diagnostics)
	{
		if (diagnostics.Count <= 0)
			return;
		
		PrintDiagnosticsOfSeverity(diagnostics, DiagnosticSeverity.Warning);
		PrintDiagnosticsOfSeverity(diagnostics, DiagnosticSeverity.Error);
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
				
				var messageParts = diagnostic.message.Split(NewlineSeparators, StringSplitOptions.None);

				foreach (var message in messageParts)
				{
					Console.ForegroundColor = ConsoleColor.DarkGray;
					Console.Write(messageHeader);
					Console.ForegroundColor = color;
					Console.WriteLine(message);
				}

				Console.ResetColor();
				Console.WriteLine();
			}
		}
	}
	
	private static async Task<CompilationResult> CompileSource(CompilationSource source)
	{
		var diagnostics = new DiagnosticList();
		var sourceBuffer = await source.GetBuffer();
		var lexer = new FilteredLexer(sourceBuffer);
		var (ast, parserDiagnostics) = Parser.Parse(lexer);
		diagnostics.Add(parserDiagnostics);
		
		if (ast is null)
		{
			lock (ReportLock)
			{
				PrintDiagnostics(diagnostics);
			}

			return new CompilationResult(diagnostics);
		}

		lock (ReportLock)
		{
			AstPrinter.Print(ast, Console.Out);
			PrintDiagnostics(diagnostics);
		}
		// Todo: Return IR ready for translation to LLVM IR
		return new CompilationResult(diagnostics);
	}
}