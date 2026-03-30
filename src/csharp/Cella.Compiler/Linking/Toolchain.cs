namespace Cella.Compiler.Linking;

public abstract class Toolchain(string root)
{
	public string Root { get; } = root;
	public abstract string LldFlavor { get; }
	
	public abstract List<string> GetLinkerArgs(LinkRequest request);
	
	public static Toolchain FromTargetTriple(TargetTriple targetTriple, string toolchainsDirectory)
	{
		// TODO Find best-case target triple instead of exact match only
		var tripleStr = targetTriple.ToString();
		var toolchainRoot = Path.Combine(toolchainsDirectory, tripleStr);
		var os = targetTriple.Os;
		var arch = targetTriple.Arch;
		
		if (arch.Equals(TargetTriple.ArchTypes.Wasm64, StringComparison.OrdinalIgnoreCase))
			throw new NotSupportedException(); //return LinkerFlavor.Wasm;
		
		if (os.Equals(TargetTriple.OsTypes.Windows, StringComparison.OrdinalIgnoreCase))
			return new WindowsMsvcToolchain(toolchainRoot);
		
		if (os.Equals(TargetTriple.OsTypes.MacOsX, StringComparison.OrdinalIgnoreCase))
			throw new NotSupportedException(); // return LinkerFlavor.MachO;
		
		return new LinuxGnuToolchain(toolchainRoot);
	}
}