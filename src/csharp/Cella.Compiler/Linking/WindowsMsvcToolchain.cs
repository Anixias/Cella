using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public sealed class WindowsMsvcToolchain(string root) : Toolchain(root)
{
	public override string LldFlavor => "link";
	
	private readonly string _msvcLibDir = Path.Combine(root, "msvc");
	private readonly string _ucrtLibDir = Path.Combine(root, "ucrt");
	private readonly string _umLibDir = Path.Combine(root, "um");
	
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
		args.Add($"/libpath:{_msvcLibDir}");
		args.Add($"/libpath:{_ucrtLibDir}");
		args.Add($"/libpath:{_umLibDir}");
		
		args.AddRange(request.InputFiles);
		
		args.Add("libcmt.lib");
		args.Add("libvcruntime.lib");
		args.Add("libucrt.lib");
		args.Add("kernel32.lib");
		
		return args;
	}
}