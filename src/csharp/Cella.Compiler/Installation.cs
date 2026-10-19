using System.Runtime.InteropServices;

namespace Cella.Compiler;

public static class Installation
{
	private static readonly Lazy<ComponentVersions?> _components =
		new(static () => ComponentVersions.Load(AppContext.BaseDirectory));
	
	public static bool IsInstalled => _components.Value is not null;
	
	private static string? Home => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
	
	public static string ToolsDirectory => _components.Value is { } components && Home is { } home
		? ComponentVersions.GetLlvmDirectory(home, components.Llvm)
		: AppContext.BaseDirectory;
	
	public static string GetToolchainDirectory(string target) =>
		_components.Value?.Toolchains.GetValueOrDefault(target) is { } version && Home is { } home
			? ComponentVersions.GetToolchainDirectory(home, target, version)
			: Path.Combine(AppContext.BaseDirectory, "toolchains", target);
	
	public static void UseComponentLibraries()
	{
		if (!IsInstalled)
			return;
		
		System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += static (_, name) =>
		{
			if (name != "libLLVM")
				return nint.Zero;
			
			var path = Path.Combine(ToolsDirectory, OperatingSystem.IsWindows() ? "LLVM-C.dll" : "libLLVM.so");
			return File.Exists(path) && NativeLibrary.TryLoad(path, out var handle) ? handle : nint.Zero;
		};
	}
}