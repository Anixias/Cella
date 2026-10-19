using System.Runtime.InteropServices;
using Cella.Core.CodeGen;

namespace Cella.Compiler;

public readonly record struct TargetTriple(string Os, string Arch, string? Abi)
{
	public override string ToString() => string.IsNullOrWhiteSpace(Abi)
		? $"{Os}-{Arch}"
		: $"{Os}-{Arch}-{Abi}";
	
	public static class OsTypes
	{
		public const string Windows = "windows";
		public const string Linux = "linux";
		public const string MacOsX = "macosx";
	}
	
	public static class Abis
	{
		public const string Msvc = "msvc";
		public const string Gnu = "gnu";
	}
	
	public static class ArchTypes
	{
		public const string X64 = "x64";
		public const string X86 = "x86";
		public const string Arm64 = "arm64";
		public const string Wasm64 = "wasm64";
	}
	
	public string ToLlvm()
	{
		var llvmArch = Arch switch
		{
			ArchTypes.X64 => "x86_64",
			ArchTypes.X86 => "i686",
			ArchTypes.Arm64 => "aarch64",
			ArchTypes.Wasm64 => "wasm64",
			_ => throw new Exception($"Unknown architecture '{Arch}'")
		};
		
		return Os switch
		{
			OsTypes.Windows => $"{llvmArch}-pc-windows-{Abi ?? "msvc"}",
			OsTypes.Linux => $"{llvmArch}-unknown-linux-{Abi ?? "gnu"}",
			OsTypes.MacOsX => $"{llvmArch}-apple-macosx",
			_ => throw new Exception($"No LLVM mapping for '{this}'")
		};
	}
	
	public TargetSystem ToSystem() => Os switch
	{
		OsTypes.Windows => TargetSystem.Windows,
		OsTypes.Linux => TargetSystem.Linux,
		OsTypes.MacOsX => TargetSystem.MacOs,
		_ => throw new Exception($"Unknown operating system '{Os}'")
	};
	
	public TargetArchitecture ToArchitecture() => Arch switch
	{
		ArchTypes.X64 => TargetArchitecture.X64,
		ArchTypes.X86 => TargetArchitecture.X86,
		ArchTypes.Arm64 => TargetArchitecture.Arm64,
		ArchTypes.Wasm64 => TargetArchitecture.Wasm64,
		_ => throw new Exception($"Unknown architecture '{Arch}'")
	};
	
	public string ToLlvmFeatures() => (Os, Arch) switch
	{
		(OsTypes.Windows or OsTypes.Linux, ArchTypes.X64) => "+cx16",
		_ => ""
	};
	
	public bool IsElf => Os == OsTypes.Linux;
	
	public uint PointerBitSize => Arch == ArchTypes.X86 ? 32u : 64u;
	
	public TargetTriple WithDefaultAbi() => Os == OsTypes.Windows && Abi is null
		? this with { Abi = HasMsvc(Arch) ? Abis.Msvc : Abis.Gnu }
		: this;
	
	public static bool HasMsvc(string arch) =>
		Directory.Exists(Installation.GetToolchainDirectory($"{OsTypes.Windows}-{arch}-{Abis.Msvc}")) &&
		Linking.MsvcLibraries.Current is not null;
	
	public static TargetTriple? Parse(string text) => text.Split('-') switch
	{
		[OsTypes.Windows, ArchTypes.X64] => new TargetTriple(OsTypes.Windows, ArchTypes.X64, null),
		[OsTypes.Windows, ArchTypes.X64, (Abis.Msvc or Abis.Gnu) and var abi] =>
			new TargetTriple(OsTypes.Windows, ArchTypes.X64, abi),
		[OsTypes.Linux, ArchTypes.X64] or [OsTypes.Linux, ArchTypes.X64, "gnu"] =>
			new TargetTriple(OsTypes.Linux, ArchTypes.X64, "gnu"),
		_ => null
	};
	
	public static TargetTriple FromHost()
	{
		var arch = RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => ArchTypes.X64,
			Architecture.X86 => ArchTypes.X86,
			Architecture.Arm64 => ArchTypes.Arm64,
			Architecture.Wasm => ArchTypes.Wasm64,
			_ => throw new PlatformNotSupportedException()
		};
		
		string os;
		string? abi;
		
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			os = "windows";
			abi = null;
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			os = "linux";
			abi = "gnu"; // TODO Attempt to detect "musl" availability?
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			os = "macosx";
			abi = null;
		}
		else
			throw new PlatformNotSupportedException();
		
		return new(os, arch, abi);
	}
}