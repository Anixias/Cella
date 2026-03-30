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
			"/subsystem:console", // TEMP This should be console or windows based on request
			$"/out:{request.OutputFile}",
			$"/libpath:{_msvcLibDir}",
			$"/libpath:{_ucrtLibDir}",
			$"/libpath:{_umLibDir}"
		};
		
		args.AddRange(request.InputFiles);
		
		args.Add("libcmt.lib");
		args.Add("libvcruntime.lib");
		args.Add("libucrt.lib");
		args.Add("kernel32.lib");
		
		return args;
	}
}