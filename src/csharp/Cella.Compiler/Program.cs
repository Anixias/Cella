using System.Text;
using Cella.Core.Analysis.Semantics;
using Cella.Core.Analysis.Syntax;
using Cella.Core.Analysis.Text;
using Cella.Core.Diagnostics;
using Mono.Options;

namespace Cella.Compiler;

internal static class Program
{
	private class Options
	{
		private readonly List<string> inputPaths = new();
		private readonly List<string> rawInputs = new();
		
		public IReadOnlyList<string> InputPaths => inputPaths;
		public IReadOnlyList<string> RawInputs => rawInputs;
		public string? OutputPath { get; private set; }
		public bool WaitOnExit { get; private set; }
		public bool PrintParseTree { get; private set; }
		
		private Options()
		{
		}
		
		public static Options? FromArgs(IEnumerable<string> args)
		{
			var options = new Options();
			var showHelp = false;
			
			var optionSet = new OptionSet
			{
				{ "o|output=", "The {OUTPUT} path", o => options.OutputPath = o },
				{ "r|raw=", "Raw input {SOURCE}", r => options.rawInputs.Add(r) },
				{ "w|wait", "Wait on exit", _ => options.WaitOnExit = true },
				{ "tree", "Print parse tree", _ => options.PrintParseTree = true },
				{ "help", "Show help", _ => showHelp = true, true },
				{ "<>", i => options.inputPaths.Add(i) }
			};
			
			try
			{
				optionSet.Parse(args);
				
				if (!showHelp)
					return options;
				
				Console.ForegroundColor = ConsoleColor.Blue;
				optionSet.WriteOptionDescriptions(Console.Out);
				Console.ResetColor();
				Environment.Exit(0);
				
				return options;
			}
			catch (OptionException e)
			{
				Console.WriteLine(e);
				Console.WriteLine("Try 'cella --help'");
				return null;
			}
		}
	}
	
	private static readonly string[] NewlineSeparators = ["\r\n", "\n", "\r"];
	
	/// <summary>
	/// Used to group several ConsoleLock locks together
	/// </summary>
	private static readonly Lock ReportLock = new();
	
	/// <summary>
	/// Used to control access to the console
	/// </summary>
	private static readonly Lock ConsoleLock = new();
	
	private static Options options = null!;
	
	private static async Task<int> Main(string[] args)
	{
		if (Options.FromArgs(args) is not { } options)
			return 1;
		
		Program.options = options;
		
		var returnCode = await Run(options);
		Console.ResetColor();
		
		if (!options.WaitOnExit)
			return returnCode;
		
		Console.WriteLine("\nPress enter to exit...");
		Console.ReadLine();
		
		return returnCode;
	}
	
	private static async Task<int> Run(Options options)
	{
		Console.OutputEncoding = Encoding.UTF8;
		
		if (options.InputPaths.Count == 0 && options.RawInputs.Count == 0)
		{
			ReportExecutionError("No inputs provided. Specify a file, a folder, or a project, or use '-r' or '--raw' " +
			                     "to input raw code directly");
			return 1;
		}
		
		var outputPath = options.OutputPath;
		if (string.IsNullOrEmpty(outputPath))
		{
			if (options.InputPaths.Count != 1)
			{
				ReportExecutionError("No output path provided. Use '-o' or '--output'");
				return 1;
			}
			
			// Todo: Handle cross-platform default extensions
			outputPath = Path.ChangeExtension(options.InputPaths[0], ".exe");
		}
		
		var inputErrors = new List<string>();
		var sources = new List<CompilationSource.IBufferSource>();
		foreach (var inputPath in options.InputPaths)
		{
			var source = CompilationSource.FromPath(inputPath);
			
			switch (source)
			{
				case null:
					inputErrors.Add($"File or directory does not exist: \"{inputPath}\"");
					break;
				
				case CompilationSource.Project project:
					if (project.Verify(out var projectErrors))
						sources.AddRange(project.GetSources());
					
					inputErrors.AddRange(projectErrors);
					continue;
				
				case CompilationSource.IMultiSource multiSource:
					sources.AddRange(multiSource.GetSources());
					continue;
				
				case CompilationSource.IBufferSource bufferSource:
					sources.Add(bufferSource);
					continue;
			}
		}
		
		foreach (var rawInput in options.RawInputs)
		{
			sources.Add(new CompilationSource.RawText(rawInput));
		}
		
		var hadInputError = inputErrors.Count > 0;
		var executionResult = await Execute(sources);
		
		lock (ConsoleLock)
		{
			if (executionResult.IsSuccess && !hadInputError)
			{
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine($"Compilation succeeded ({Format(executionResult.ElapsedTime)})");
				return 0;
			}
			
			if (hadInputError)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("Input Errors:");
				foreach (var error in inputErrors)
				{
					Console.WriteLine($"\t{error}");
				}
				
				Console.WriteLine();
				Console.ResetColor();
			}
			
			Console.ForegroundColor = ConsoleColor.DarkRed;
			Console.WriteLine("Failed to compile");
			return 1;
		}
	}
	
	private static void ReportExecutionError(string message)
	{
		lock (ConsoleLock)
		{
			Console.ResetColor();
			Console.ForegroundColor = ConsoleColor.DarkRed;
			Console.Write("Error: ");
			Console.ResetColor();
			Console.WriteLine(message);
			Console.ResetColor();
		}
	}
	
	private static string Format(TimeSpan time)
	{
		if (time < TimeSpan.FromMicroseconds(1.0))
			return $"{time.TotalNanoseconds:F2} ns";
		
		if (time < TimeSpan.FromMilliseconds(1.0))
			return $"{time.TotalMicroseconds:F2} µs";
		
		if (time < TimeSpan.FromSeconds(1.0))
			return $"{time.TotalMilliseconds:F2} ms";
		
		if (time < TimeSpan.FromMinutes(1.0))
			return $"{time.TotalSeconds:F2} s";
		
		if (time < TimeSpan.FromHours(1.0))
			return $"{time.TotalMinutes:F2} min";
		
		return $"{time.TotalHours:F2} h";
	}
	
	private static async Task<ExecutionResult> Execute(IEnumerable<CompilationSource.IBufferSource> compilationSources)
	{
		var sources = compilationSources.ToArray();
		var globalScope = NativeSymbolHandler.CreateGlobalScope();
		var startTime = DateTime.UtcNow;
		
		var collectionTasks = sources.Select(s => CollectSource(s, globalScope)).ToArray();
		var collectionResults = await Task.WhenAll(collectionTasks);
		if (collectionResults.Any(t => t is null))
			return new ExecutionResult(false, DateTime.UtcNow - startTime);
		
		var resolutionTasks = collectionResults.Select(a => ResolveSource(a!, globalScope));
		var resolutionResults = await Task.WhenAll(resolutionTasks);
		
		var endTime = DateTime.UtcNow;
		
		var isSuccess = resolutionResults.All(r => r.IsSuccess);
		var elapsedTime = endTime - startTime;
		
		return new ExecutionResult(isSuccess, elapsedTime);
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
			var simpleEncoding = !CanBeEncoded("\u2191");
			var lineBar = simpleEncoding ? '|' : '\u2502';
			var upArrow = simpleEncoding ? '^' : '\u2191';
			var eofStr = simpleEncoding ? "(EOF)" : "·";
			//const char downArrow = '\u2193';
			
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
					if (diagnostic.range != TextRange.EndOfFile)
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
							Console.WriteLine();
						
						Console.ForegroundColor = ConsoleColor.DarkGray;
						Console.Write(messageHeader);
						Console.ForegroundColor = darkColor;
						var column = diagnostic.source.GetLineColumn(range.Start).column - (columnSkips + 1);
						Console.Write(new string(' ', column));
						Console.WriteLine(new string(upArrow, range.Length));
					}
					else
					{
						var columnSkips = 0;
						var preRange = new TextRange(lineRange.Start, lineRange.End);
						while (char.IsWhiteSpace(diagnostic.source[preRange.Start]))
						{
							preRange = new TextRange(preRange.Start + 1, preRange.End);
							columnSkips++;
						}
						
						if (diagnostic.line > 0)
						{
							Console.Write(diagnostic.source.GetText(preRange));
							Console.ForegroundColor = darkColor;
							Console.WriteLine(eofStr);
						}
						else
							Console.WriteLine();
						
						Console.ForegroundColor = ConsoleColor.DarkGray;
						Console.Write(messageHeader);
						Console.ForegroundColor = darkColor;
						var column = diagnostic.source.GetLineColumn(preRange.End).column - (columnSkips + 1);
						Console.Write(new string(' ', column));
						Console.WriteLine(new string(upArrow, range.Length));
					}
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
	
	private static bool CanBeEncoded(string text)
	{
		try
		{
			Encoding.GetEncoding(Console.OutputEncoding.CodePage, EncoderFallback.ExceptionFallback,
				DecoderFallback.ExceptionFallback).GetBytes(text);
			
			return true;
		}
		catch (EncoderFallbackException)
		{
			return false;
		}
		catch (NotSupportedException)
		{
			return false;
		}
	}
	
	private static async Task<TypedAst?> CollectSource(CompilationSource.IBufferSource source, Scope globalScope)
	{
		var diagnostics = new DiagnosticList();
		var sourceBuffer = await source.GetBuffer();
		var lexer = new FilteredLexer(sourceBuffer);
		var (ast, parserDiagnostics) = await Task.Run(() => Parser.Parse(lexer));
		diagnostics.Add(parserDiagnostics);
		
		if (ast is null)
		{
			lock (ReportLock)
			{
				PrintDiagnostics(diagnostics);
			}
			
			return null;
		}
		
		if (options.PrintParseTree)
		{
			lock (ConsoleLock)
			{
				AstPrinter.Print(ast, Console.Out);
			}
		}
		
		var (typedAst, collectorDiagnostics) = await Task.Run(() => Collector.Collect(globalScope, ast));
		diagnostics.Add(collectorDiagnostics);
		
		if (typedAst is not null)
			return typedAst;
		
		lock (ReportLock)
		{
			PrintDiagnostics(diagnostics);
		}
		
		return null;
	}
	
	private static async Task<CompilationResult> ResolveSource(TypedAst ast, Scope globalScope)
	{
		var diagnostics = new DiagnosticList();
		var (resolvedAst, resolverDiagnostics) = await Task.Run(() => Resolver.Resolve(globalScope, ast));
		diagnostics.Add(resolverDiagnostics);
		
		if (resolvedAst is null)
		{
			lock (ReportLock)
			{
				PrintDiagnostics(diagnostics);
			}
			
			return new CompilationResult(diagnostics);
		}
		
		lock (ReportLock)
		{
			PrintDiagnostics(diagnostics);
		}
		
		// Todo: Return IR ready for translation to LLVM IR
		return new CompilationResult(diagnostics);
	}
}