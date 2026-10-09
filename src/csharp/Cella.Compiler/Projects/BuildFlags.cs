using Cella.Core.CodeGen;
using Cella.Core.Text;

namespace Cella.Compiler.Projects;

public static class BuildFlags
{
	public static Dictionary<string, bool> Create(TargetTriple target, OptimizeMode mode) => new()
	{
		["windows"] = target.Os == TargetTriple.OsTypes.Windows,
		["linux"] = target.Os == TargetTriple.OsTypes.Linux,
		["macos"] = target.Os == TargetTriple.OsTypes.MacOsX,
		["x64"] = target.Arch == TargetTriple.ArchTypes.X64,
		["x86"] = target.Arch == TargetTriple.ArchTypes.X86,
		["arm64"] = target.Arch == TargetTriple.ArchTypes.Arm64,
		["wasm64"] = target.Arch == TargetTriple.ArchTypes.Wasm64,
		["debug"] = mode == OptimizeMode.Debug,
		["release"] = mode == OptimizeMode.Release
	};
	
	public static bool IsValidName(string name) =>
		new FilteredScanner(new StringSource(name, name)).ToList() is
			[{ Type: TokenType.Identifier } token, { Type: TokenType.EndOfFile }] && token.Text == name;
}