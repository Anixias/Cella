namespace Cella.Compiler.Linking;

public sealed class LinuxGnuToolchain(string root) : Toolchain(root)
{
	public override string LldFlavor => "gnu";
	
	private readonly string _sysrootDir = Path.Combine(root, "sysroot");
	private readonly string _libDir = Path.Combine(root, "sysroot", "usr", "lib");
	
	public override Dictionary<string, string?> GetLinkerEnvironmentVars(LinkRequest request) => [];
	
	public override List<string> GetLinkerArgs(LinkRequest request)
	{
		var args = new List<string>
		{
			$"--sysroot={_sysrootDir}",
			$"-o {request.OutputFile}",
			Path.Combine(_libDir, "crt1.o"),
			Path.Combine(_libDir, "crti.o"),
		};
		
		args.AddRange(request.InputFiles);
		
		args.Add("-lc");
		args.Add(Path.Combine(_libDir, "crtn.o"));
		
		args.AddRange(request.LibFiles.Distinct());
		
		return args;
	}
}