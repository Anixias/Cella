using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public sealed class LinuxGnuToolchain(string root) : Toolchain
{
	private const string DynamicLinker = "/lib64/ld-linux-x86-64.so.2";
	
	private readonly string _sysroot = Path.Combine(root, "sysroot");
	private readonly string _compilerRt = Path.Combine(root, "compiler-rt");
	
	public override string GetTool(ProjectOutputType outputType) =>
		outputType == ProjectOutputType.StaticLibrary ? "llvm-ar" : "lld";
	
	public override Dictionary<string, string?> GetLinkerEnvironmentVars() => [];
	
	public override List<string> GetLeadingLinkerArgs(ProjectOutputType outputType) =>
		outputType == ProjectOutputType.StaticLibrary ? [] : ["-flavor", "gnu"];
	
	public override ResponseFileQuoting GetResponseFileQuoting(ProjectOutputType outputType) => HostQuoting;
	
	public override List<string> GetLinkerArgs(LinkRequest request)
	{
		if (request.OutputType == ProjectOutputType.StaticLibrary)
			return ["rcsD", request.OutputFile, ..request.InputFiles];
		
		var libraries = Path.Combine(_sysroot, "usr", "lib64");
		var isShared = request.OutputType == ProjectOutputType.SharedLibrary;
		var args = new List<string>
		{
			$"--sysroot={_sysroot}",
			"-m", "elf_x86_64",
			"--eh-frame-hdr",
			"--hash-style=gnu",
			"--build-id",
			"-z", "relro",
			"-z", "now",
			"-rpath", "$ORIGIN",
			"-o", request.OutputFile
		};
		
		if (isShared)
			args.AddRange(["-shared", "-soname", Path.GetFileName(request.OutputFile)]);
		else
			args.AddRange([
				"-pie", "-dynamic-linker", DynamicLinker, "--export-dynamic",
				Path.Combine(libraries, "Scrt1.o")
			]);
		
		foreach (var symbol in request.KeptSymbols)
			args.AddRange(["-u", symbol]);
		
		args.Add(Path.Combine(libraries, "crti.o"));
		args.Add(Path.Combine(_compilerRt, "clang_rt.crtbegin.o"));
		args.AddRange(request.InputFiles);
		args.AddRange(request.LibFiles.Distinct());
		args.AddRange(["-L", libraries, "-L", Path.Combine(_sysroot, "lib64"), "-lc", "-lm"]);
		args.Add(Path.Combine(_compilerRt, "libclang_rt.builtins.a"));
		args.Add(Path.Combine(_compilerRt, "clang_rt.crtend.o"));
		args.Add(Path.Combine(libraries, "crtn.o"));
		return args;
	}
}