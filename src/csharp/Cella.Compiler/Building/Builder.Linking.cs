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
	private static DiagnosticList? DescribeLinkErrors(string errors,
		IEnumerable<(AssemblySymbol Assembly, string Directory)> assemblies, TargetTriple target, Toolchain toolchain)
	{
		var declarations = new Dictionary<string, List<SourceLocation>>();
		var origins = new Dictionary<string, List<(string Origin, SourceLocation Location)>>(
			StringComparer.OrdinalIgnoreCase);
		
		foreach (var (assembly, directory) in assemblies)
		{
			foreach (var symbol in assembly.SymbolTable.DeclarationSymbols.Values.Distinct())
			{
				var (name, identifier, origin, originLocation) = symbol switch
				{
					FunctionSymbol { Syntax: ExternalFunctionNode node } function => (function.ExternalName ??
						function.Name, node.Identifier.SourceLocation, node.Origin, node.OriginLocation),
					GlobalSymbol { IsImport: true } global => (global.ExternalName ?? global.Name,
						global.Syntax.Identifier.SourceLocation, global.Syntax.Origin, global.Syntax.OriginLocation),
					_ => default
				};
				
				if (name is null)
					continue;
				
				declarations.GetOrAdd(name).Add(identifier);
				if (origin is not null && originLocation is { } location)
					origins.GetOrAdd(toolchain.ToLinkerInput(ResolveLibrary(origin, directory, target)))
						.Add((origin, location));
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
			    is var symbol && declarations.TryGetValue(symbol, out var declared))
			{
				if (reported.Add(symbol))
					foreach (var declaration in declared.Distinct())
						diagnostics.Add(new(DiagnosticSeverity.Error, declaration,
							$"Cannot find '{symbol}' in the linked libraries"));
				
				continue;
			}
			
			if (FindMissingLibrary(line) is { } missing && origins.TryGetValue(missing, out var libraries))
			{
				if (reported.Add(missing))
					foreach (var (origin, location) in libraries.Distinct())
						diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot find the library '{origin}'"));
				
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
			return line[MissingFile.Length..end];
		
		if (line.StartsWith(MissingLibrary, StringComparison.Ordinal))
			return line[MissingLibrary.Length..];
		
		return line.StartsWith(MissingInput, StringComparison.Ordinal) &&
		       line.LastIndexOf(": ", StringComparison.Ordinal) is var colon and > 0
			? line[MissingInput.Length..colon]
			: null;
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
		foreach (var library in dependencies.Where(static d => d.OutputType != ProjectOutputType.StaticLibrary))
		{
			if (library.OutputPath is { } path)
				File.Copy(path, Path.Combine(outputDir, Path.GetFileName(path)), true);
			
			if (library.DebugSymbols is { } symbols)
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