using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public abstract class Toolchain
{
	public abstract string GetTool(ProjectOutputType outputType);
	public abstract Dictionary<string, string?> GetLinkerEnvironmentVars();
	public abstract List<string> GetLinkerArgs(LinkRequest request);
	
	public static Toolchain FromTargetTriple(TargetTriple targetTriple)
	{
		var toolchainRoot = Installation.GetToolchainDirectory(targetTriple.ToString());
		return targetTriple.Os switch
		{
			TargetTriple.OsTypes.Windows when targetTriple.Abi == TargetTriple.Abis.Gnu =>
				new WindowsGnuToolchain(toolchainRoot),
			TargetTriple.OsTypes.Windows => new WindowsMsvcToolchain(toolchainRoot, MsvcLibraries.Current,
				$"clang_rt.builtins-{
					targetTriple.Arch switch
					{
						TargetTriple.ArchTypes.X86 => "i386",
						TargetTriple.ArchTypes.Arm64 => "aarch64",
						_ => "x86_64"
					}}.lib"),
			TargetTriple.OsTypes.Linux => new LinuxGnuToolchain(toolchainRoot),
			_ => throw new NotSupportedException()
		};
	}
}