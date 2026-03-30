namespace Cella.Compiler.Linking;

public abstract class Toolchain(string root)
{
	public string Root { get; } = root;
	public abstract string LldFlavor { get; }
	
	public abstract List<string> GetLinkerArgs(LinkRequest request);
	
	public static Toolchain FromTargetTriple(string targetTriple, string toolchainsDirectory)
	{
		// TODO Find best-case target triple instead of exact match only
		var toolchainRoot = Path.Combine(toolchainsDirectory, targetTriple);
		var tripleParts = targetTriple.Split('-');
		var os = tripleParts[2];
		
		if (os.Equals("windows", StringComparison.OrdinalIgnoreCase))
			return new WindowsMsvcToolchain(toolchainRoot);
		
		if (os.Equals("darwin", StringComparison.OrdinalIgnoreCase) ||
		    os.Equals("apple", StringComparison.OrdinalIgnoreCase) ||
		    os.Equals("macos", StringComparison.OrdinalIgnoreCase))
			throw new NotSupportedException(); // return LinkerFlavor.MachO;
		
		if (os.Equals("wasm", StringComparison.OrdinalIgnoreCase))
			throw new NotSupportedException(); //return LinkerFlavor.Wasm;
		
		return new LinuxGnuToolchain(toolchainRoot);
	}
}