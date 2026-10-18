using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cella.Compiler;

public static class Installation
{
	public const string ComponentsFileName = "components.json";
	
	private static readonly Lazy<IReadOnlyDictionary<string, string>?> _components = new(LoadComponents);
	
	public static string HostName => (OperatingSystem.IsWindows() ? "win" : "linux") + "-" +
	                                 RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
	
	public static bool IsInstalled => _components.Value is not null;
	
	public static string ToolsDirectory => FindComponent($"llvm-{HostName}", "llvm") ?? AppContext.BaseDirectory;
	
	public static string GetToolchainDirectory(string target) =>
		FindComponent(target, Path.Combine("toolchains", target)) ??
		Path.Combine(AppContext.BaseDirectory, "toolchains", target);
	
	private static string? FindComponent(string name, string folder)
	{
		if (_components.Value?.GetValueOrDefault(name) is not { } version)
			return null;
		
		var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
		return root is null ? null : Path.Combine(root, folder, version);
	}
	
	private static IReadOnlyDictionary<string, string>? LoadComponents()
	{
		var path = Path.Combine(AppContext.BaseDirectory, ComponentsFileName);
		if (!File.Exists(path))
			return null;
		
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		return document.RootElement.EnumerateObject()
			.Where(static property => property.Value.ValueKind == JsonValueKind.String)
			.ToDictionary(static property => property.Name, static property => property.Value.GetString()!);
	}
	
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