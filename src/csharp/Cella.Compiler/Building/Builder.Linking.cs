using System.Collections.Immutable;
using Cella.Compiler.Linking;
using Cella.Compiler.Projects;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Compiler.Building;

public sealed partial class Builder
{
	private static DiagnosticList? DescribeLinkErrors(string errors, IEnumerable<AssemblySymbol> assemblies)
	{
		var declarations = new Dictionary<string, SourceLocation>();
		var origins =
			new Dictionary<string, (string Origin, SourceLocation Location)>(StringComparer.OrdinalIgnoreCase);
		
		foreach (var symbol in assemblies.SelectMany(static assembly => assembly.SymbolTable.DeclarationSymbols.Values))
		{
			switch (symbol)
			{
				case FunctionSymbol { Syntax: ExternalFunctionNode node } function:
					declarations.TryAdd(function.ExternalName ?? function.Name, node.Identifier.SourceLocation);
					if (node is { Origin: { } origin, OriginLocation: { } location })
						foreach (var key in GetLibraryKeys(origin))
							origins.TryAdd(key, (origin, location));
					
					break;
				
				case GlobalSymbol { IsImport: true } global:
					declarations.TryAdd(global.ExternalName ?? global.Name, global.Syntax.Identifier.SourceLocation);
					if (global.Syntax is { Origin: { } globalOrigin, OriginLocation: { } globalLocation })
						foreach (var key in GetLibraryKeys(globalOrigin))
							origins.TryAdd(key, (globalOrigin, globalLocation));
					
					break;
			}
		}
		
		var diagnostics = new DiagnosticList();
		var reported = new HashSet<string>();
		foreach (var line in errors.Split('\n').Select(static line => line.Trim().Replace(ForcedSymbolPrefix, ""))
			         .Where(static line => line.Length > 0))
		{
			if (line.StartsWith(">>>", StringComparison.Ordinal) ||
			    line.StartsWith(LinkerWarning, StringComparison.Ordinal))
				continue;
			
			if (line.StartsWith(UndefinedSymbol, StringComparison.Ordinal) &&
			    line[UndefinedSymbol.Length..] is var undefined &&
			    (undefined.StartsWith(ImportPrefix, StringComparison.Ordinal)
				    ? undefined[ImportPrefix.Length..]
				    : undefined)
			    is var symbol && declarations.TryGetValue(symbol, out var declaration))
			{
				if (reported.Add(symbol))
					diagnostics.Add(new(DiagnosticSeverity.Error, declaration,
						$"Cannot find '{symbol}' in the linked libraries"));
				
				continue;
			}
			
			if (FindMissingLibrary(line) is { } missing && origins.TryGetValue(missing, out var library))
			{
				diagnostics.Add(new(DiagnosticSeverity.Error, library.Location,
					$"Cannot find the library '{library.Origin}'"));
				
				continue;
			}
			
			return null;
		}
		
		return diagnostics.Count > 0 ? diagnostics : null;
	}
	
	private const string UndefinedSymbol = "lld: error: undefined symbol: ";
	private const string ForcedSymbolPrefix = "<root>: ";
	private const string LinkerWarning = "lld: warning: ";
	private const string MissingFile = "lld: error: could not open '";
	private const string MissingLibrary = "lld: error: unable to find library ";
	private const string MissingInput = "lld: error: cannot open ";
	
	private static string? FindMissingLibrary(string line)
	{
		if (line.StartsWith(MissingFile, StringComparison.Ordinal) &&
		    line.IndexOf('\'', MissingFile.Length) is var end and > 0)
			return Path.GetFileName(line[MissingFile.Length..end]);
		
		if (line.StartsWith(MissingLibrary, StringComparison.Ordinal))
			return line[MissingLibrary.Length..];
		
		return line.StartsWith(MissingInput, StringComparison.Ordinal) &&
		       line.LastIndexOf(": ", StringComparison.Ordinal) is var colon and > 0
			? Path.GetFileName(line[MissingInput.Length..colon])
			: null;
	}
	
	private static IEnumerable<string> GetLibraryKeys(string origin)
	{
		var name = Path.GetFileName(origin);
		return [Path.GetFileName(GetLibraryFileName(origin)), $"-l{name}", $"lib{name}.so", name];
	}
	
	private const string ImportPrefix = "__declspec(dllimport) ";
	
	private static IEnumerable<string> FindStaticGlobals(IEnumerable<string> globals, IEnumerable<string?> libraries,
		TargetTriple target)
	{
		if (target.IsElf)
			return [];
		
		var defined = libraries.OfType<string>()
			.Where(static library => Path.IsPathRooted(library) && File.Exists(library))
			.SelectMany(StaticArchive.ReadObjectDefinitions)
			.ToHashSet(StringComparer.Ordinal);
		
		return globals.Where(defined.Contains);
	}
	
	private static string ResolveLibrary(string origin, string projectDirectory, TargetTriple target)
	{
		if (target.IsElf)
		{
			var directory = Path.GetDirectoryName(origin) ?? "";
			var name = Path.GetFileName(origin);
			string[] candidates = Path.HasExtension(name) ? [name] : [$"lib{name}.so", $"lib{name}.a"];
			foreach (var candidate in candidates)
			{
				var path = Path.Combine(projectDirectory, directory, candidate);
				if (File.Exists(path))
					return Path.GetFullPath(path);
			}
			
			return directory.Length == 0 && !Path.HasExtension(name)
				? $"-l{name}"
				: Path.GetFullPath(Path.Combine(projectDirectory, directory, candidates[0]));
		}
		
		var fileName = GetLibraryFileName(origin);
		var local = Path.Combine(projectDirectory, fileName);
		return File.Exists(local) ? Path.GetFullPath(local) : fileName;
	}
	
	private static string GetLibraryFileName(string name) =>
		Path.GetExtension(name).ToLowerInvariant() is ".lib" or ".dll"
			? Path.ChangeExtension(name, ".lib")
			: $"{name}.lib";
	
	private static void CopySharedLibraries(ImmutableArray<AssemblyInfo> dependencies, string outputDir)
	{
		var libraries = dependencies.Where(static d => d.OutputType != ProjectOutputType.StaticLibrary);
		foreach (var path in libraries.Select(static d => d.OutputPath).OfType<string>())
		{
			File.Copy(path, Path.Combine(outputDir, Path.GetFileName(path)), true);
			var symbols = Path.ChangeExtension(path, ".pdb");
			if (File.Exists(symbols))
				File.Copy(symbols, Path.Combine(outputDir, Path.GetFileName(symbols)), true);
		}
	}
	
	private static string WriteNatvis(string directory)
	{
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "cella.natvis");
		using var source = typeof(Builder).Assembly.GetManifestResourceStream("cella.natvis")!;
		using var target = File.Create(path);
		source.CopyTo(target);
		return path;
	}
	
	private static string GetOutputFileName(string baseName, TargetTriple target, ProjectOutputType outputType)
	{
		var ext = GetOutputExtension(target, outputType);
		
		var needsLibPrefix =
			!outputType.IsExecutable() &&
			target.Os is TargetTriple.OsTypes.Linux or TargetTriple.OsTypes.MacOsX;
		
		return needsLibPrefix
			? $"lib{baseName}{ext}"
			: $"{baseName}{ext}";
	}
	
	private static string? GetOutputExtension(TargetTriple target, ProjectOutputType outputType) => target.Os switch
	{
		TargetTriple.OsTypes.Windows => outputType switch
		{
			ProjectOutputType.Executable or ProjectOutputType.WindowedExecutable => ".exe",
			ProjectOutputType.StaticLibrary => ".lib",
			_ => ".dll"
		},
		TargetTriple.OsTypes.Linux => outputType switch
		{
			ProjectOutputType.Executable or ProjectOutputType.WindowedExecutable => null,
			ProjectOutputType.StaticLibrary => ".a",
			_ => ".so"
		},
		TargetTriple.OsTypes.MacOsX => outputType switch
		{
			ProjectOutputType.Executable or ProjectOutputType.WindowedExecutable => null,
			ProjectOutputType.StaticLibrary => ".a",
			_ => ".dylib"
		},
		_ => null
	};
}