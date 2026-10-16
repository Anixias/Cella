using Cella.Compiler.Projects;
using Cella.Core.CodeGen;

namespace Cella.Compiler.Linking;

public sealed class WindowsMsvcToolchain(string root, string builtinsLibrary) : Toolchain
{
	public override string GetTool(ProjectOutputType outputType) =>
		outputType == ProjectOutputType.StaticLibrary ? "llvm-lib" : "lld";
	
	private readonly string _msvcLibDir = Path.Combine(root, "msvc");
	private readonly string _ucrtLibDir = Path.Combine(root, "ucrt");
	private readonly string _umLibDir = Path.Combine(root, "um");
	private readonly string _compilerRtLibDir = Path.Combine(root, "compiler-rt");
	
	public override Dictionary<string, string?> GetLinkerEnvironmentVars() => new()
	{
		["LIB"] = string.Join(';', _msvcLibDir, _ucrtLibDir, _umLibDir, _compilerRtLibDir),
		["LINK"] = string.Empty,
		["_LINK_"] = string.Empty,
	};
	
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
		
		if (request.DebugInfo && request.OutputType != ProjectOutputType.StaticLibrary)
		{
			args.AddRange(["/debug", "/pdbaltpath:%_PDB%", "/opt:ref"]);
			if (request.OptimizeMode == OptimizeMode.Release)
				args.Add("/opt:icf");
		}
		
		args.AddRange(request.InputFiles);
		if (request.ModuleDefinition is { } definition)
			args.Add($"/def:{definition}");
		
		if (request.OutputType != ProjectOutputType.StaticLibrary)
		{
			args.Add(request.LinkPreference == SystemLinkPreference.Static ? "libcmt.lib" : "msvcrt.lib");
			args.Add("legacy_stdio_definitions.lib");
			args.Add(builtinsLibrary);
		}
		
		args.AddRange(request.LibFiles.Distinct());
		
		return args;
	}
}