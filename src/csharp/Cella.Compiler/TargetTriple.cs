using System.Runtime.InteropServices;

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
	
	public static class ArchTypes
	{
		public const string X64 = "x64";
		public const string X86 = "x86";
		public const string Arm64 = "arm64";
		public const string Wasm64 = "wasm64";
	}
	
	public static TargetTriple Parse(string targetTriple)
	{
		var parts = targetTriple.ToLowerInvariant().Split('-');
		if (parts.Length is < 2 or > 3)
			throw new FormatException();
		
		return new(parts[0], parts[1], parts.Length < 3 ? null : parts[2]);
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
			abi = "msvc";
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