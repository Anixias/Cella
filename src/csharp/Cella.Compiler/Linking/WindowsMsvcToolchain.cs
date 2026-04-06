using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public sealed class WindowsMsvcToolchain(string root) : Toolchain(root)
{
	public override string LldFlavor => "link";
	
	private readonly string _msvcLibDir = Path.Combine(root, "msvc");
	private readonly string _ucrtLibDir = Path.Combine(root, "ucrt");
	private readonly string _umLibDir = Path.Combine(root, "um");
	
	public override Dictionary<string, string?> GetLinkerEnvironmentVars(LinkRequest request) => new()
	{
		["LIB"] = string.Join(';', _msvcLibDir, _ucrtLibDir, _umLibDir),
		["LINK"] = string.Empty,
		["_LINK_"] = string.Empty,
	};
	
	public override List<string> GetLinkerArgs(LinkRequest request)
	{
		var args = new List<string>
		{
			"/nologo",
			"/subsystem:console" // TEMP This should be console or windows based on request
		};
		
		if (request.OutputType == ProjectOutputType.SharedLibrary)
			args.Add("/dll");
		
		args.Add($"/out:{request.OutputFile}");
		
		args.AddRange(request.InputFiles);
		
		if (request.OutputType is ProjectOutputType.Executable or ProjectOutputType.SharedLibrary)
			args.Add(request.LinkPreference == SystemLinkPreference.Static ? "libcmt.lib" : "msvcrt.lib");
		
		args.AddRange(request.LibFiles.Distinct());
		
		return args;
	}
}