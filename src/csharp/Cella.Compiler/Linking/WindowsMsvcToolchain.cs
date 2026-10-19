using Cella.Compiler.Projects;
using Cella.Core.CodeGen;

namespace Cella.Compiler.Linking;

public sealed class WindowsMsvcToolchain(string root, MsvcLibraries? libraries, string builtinsLibrary) : Toolchain
{
	public override string GetTool(ProjectOutputType outputType) =>
		outputType == ProjectOutputType.StaticLibrary ? "llvm-lib" : "lld";
	
	private readonly string _builtinsPath = Path.Combine(root, "compiler-rt", builtinsLibrary);
	
	public override Dictionary<string, string?> GetLinkerEnvironmentVars() => new()
	{
		["LIB"] = libraries is null ? string.Empty : string.Join(';', libraries.Msvc, libraries.Ucrt, libraries.Um),
		["LINK"] = string.Empty,
		["_LINK_"] = string.Empty,
	};
	
	public override string? GetDebugSymbolsPath(LinkRequest request) =>
		request.DebugInfo && request.OutputType != ProjectOutputType.StaticLibrary
			? Path.ChangeExtension(request.OutputFile, ".pdb")
			: null;
	
	public override List<string> GetLinkerArgs(LinkRequest request)
	{
		var args = request.OutputType == ProjectOutputType.StaticLibrary
			? new List<string> { "/nologo" }
			: ["-flavor", "link", "/nologo"];
		
		if (request.OutputType == ProjectOutputType.WindowedExecutable)
			args.AddRange(["/subsystem:windows", "/entry:mainCRTStartup"]);
		else if (request.OutputType is ProjectOutputType.Executable or ProjectOutputType.SharedLibrary)
			args.Add("/subsystem:console");
		
		if (request.OutputType == ProjectOutputType.SharedLibrary)
			args.Add("/dll");
		
		if (request.OutputType != ProjectOutputType.StaticLibrary)
			args.Add("/Brepro");
		
		args.Add($"/out:{request.OutputFile}");
		
		if (GetDebugSymbolsPath(request) is { } symbols)
		{
			args.AddRange(["/debug", $"/pdb:{symbols}", "/pdbaltpath:%_PDB%", "/opt:ref"]);
			if (request.OptimizeMode == OptimizeMode.Release)
				args.Add("/opt:icf");
			
			if (request.Natvis is { } natvis)
				args.Add($"/natvis:{natvis}");
		}
		
		args.AddRange(request.InputFiles);
		if (request.OutputType != ProjectOutputType.StaticLibrary)
			args.AddRange(request.ForcedSymbols.Select(static symbol => $"/include:{symbol}"));
		
		if (request.ModuleDefinition is { } definition)
			args.Add($"/def:{definition}");
		
		if (request.OutputType != ProjectOutputType.StaticLibrary)
		{
			args.Add(request.LinkPreference == SystemLinkPreference.Static ? "libcmt.lib" : "msvcrt.lib");
			args.Add("legacy_stdio_definitions.lib");
			args.Add(_builtinsPath);
		}
		
		args.AddRange(request.LibFiles.Distinct());
		
		return args;
	}
}