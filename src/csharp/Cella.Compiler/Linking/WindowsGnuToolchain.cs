using Cella.Compiler.Projects;
using Cella.Core.CodeGen;

namespace Cella.Compiler.Linking;

public sealed class WindowsGnuToolchain(string root) : Toolchain
{
	private const string StackReserve = "1048576";
	
	private readonly string _mingw = Path.Combine(root, "mingw");
	private readonly string _builtins = Path.Combine(root, "compiler-rt", "libclang_rt.builtins-x86_64.a");
	
	public override string GetTool(ProjectOutputType outputType) =>
		outputType == ProjectOutputType.StaticLibrary ? "llvm-lib" : "lld";
	
	public override Dictionary<string, string?> GetLinkerEnvironmentVars() => [];
	
	public override List<string> GetLeadingLinkerArgs(ProjectOutputType outputType) =>
		outputType == ProjectOutputType.StaticLibrary ? [] : ["-flavor", "gnu", "-m", "i386pep"];
	
	public override ResponseFileQuoting GetResponseFileQuoting(ProjectOutputType outputType) =>
		outputType == ProjectOutputType.StaticLibrary ? ResponseFileQuoting.Windows : HostQuoting;
	
	public override string? GetDebugSymbolsPath(LinkRequest request) =>
		request.DebugInfo ? Path.ChangeExtension(request.OutputFile, ".pdb") : null;
	
	public override List<string> GetLinkerArgs(LinkRequest request)
	{
		if (request.OutputType == ProjectOutputType.StaticLibrary)
			return ["/nologo", $"/out:{request.OutputFile}", ..request.InputFiles];
		
		var isShared = request.OutputType == ProjectOutputType.SharedLibrary;
		var args = new List<string>
		{
			"-o", request.OutputFile,
			"--subsystem", request.OutputType == ProjectOutputType.WindowedExecutable ? "windows" : "console",
			"--stack", StackReserve,
			"--gc-sections",
			"-Xlink=-Brepro"
		};
		
		foreach (var symbol in request.ForcedSymbols)
			args.AddRange(["-u", symbol]);
		
		if (isShared)
			args.AddRange([
				"--shared", "-e", "DllMainCRTStartup",
				$"--out-implib={Path.ChangeExtension(request.OutputFile, ".lib")}"
			]);
		
		if (GetDebugSymbolsPath(request) is { } symbols)
			args.AddRange([$"--pdb={symbols}", "-Xlink=-pdbaltpath:%_PDB%"]);
		
		if (!request.DebugInfo || request.OptimizeMode == OptimizeMode.Release)
			args.Add("--icf=all");
		
		args.Add(Path.Combine(_mingw, isShared ? "dllcrt2.o" : request.WideEntry ? "crt2u.o" : "crt2.o"));
		args.Add(Path.Combine(_mingw, "crtbegin.o"));
		args.AddRange(request.InputFiles);
		if (request.ModuleDefinition is { } definition)
			args.Add(definition);
		
		args.AddRange(request.LibFiles.Distinct().Select(ToLinkerInput));
		args.AddRange([
			"-L", _mingw,
			"--start-group",
			"-lmingw32", _builtins, "-lmoldname", "-lmingwex", "-lucrt",
			"-ladvapi32", "-lshell32", "-luser32", "-lkernel32",
			"--end-group",
			Path.Combine(_mingw, "crtend.o")
		]);
		
		return args;
	}
	
	public override string ToLinkerInput(string library) =>
		Path.GetDirectoryName(library) is "" &&
		Path.GetExtension(library).Equals(".lib", StringComparison.OrdinalIgnoreCase)
			? $"-l{Path.GetFileNameWithoutExtension(library)}"
			: library;
}