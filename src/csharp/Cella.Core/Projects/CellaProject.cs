using CsToml;

namespace Cella.Compiler.Projects;

[TomlSerializedObject]
public sealed partial class CellaProject
{
	public const string ProjectFileExtension = ".celp";
	public const string SourceFileExtension = ".ce";
	private const string ProjectSearchPattern = $"*{ProjectFileExtension}";
	private const string SourceSearchPattern = $"*{SourceFileExtension}";
	
	[TomlValueOnSerialized]
	public required ProjectOutputType OutputType { get; init; }
	
	[TomlValueOnSerialized]
	public string? AssemblyName { get; init; }
	
	public static IEnumerable<string> FindProjects(string directory) =>
		Directory.EnumerateFiles(directory, ProjectSearchPattern, SearchOption.AllDirectories);
	
	// All cella files in or under the project directory belong to that project
	public static IEnumerable<string> FindSourceFiles(string directory) => FindSourceFiles(directory, true);
	
	public static async Task<CellaProject> LoadAsync(Stream stream) =>
		await CsTomlSerializer.DeserializeAsync<CellaProject>(stream);
	
	private static IEnumerable<string> FindSourceFiles(string directory, bool allowProjectFile)
	{
		if (!allowProjectFile && Directory.EnumerateFiles(directory, ProjectSearchPattern).Any())
			yield break;
		
		foreach (var file in Directory.EnumerateFiles(directory, SourceSearchPattern))
			yield return file;
		
		foreach (var subdirectory in Directory.EnumerateDirectories(directory))
			foreach (var file in FindSourceFiles(subdirectory, false))
				yield return file;
	}
}

public enum ProjectOutputType
{
	Executable,
	SharedLibrary,
	StaticLibrary
}