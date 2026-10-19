using Cella.Compiler;
using Cella.Compiler.Projects;

namespace Cella.Launcher;

internal static class Pins
{
	public static SemanticVersion? FindHighest(string path)
	{
		IEnumerable<string> projects = File.Exists(path)
			? [path]
			: Directory.Exists(path)
				? Directory.EnumerateFiles(path, "*.celp", SearchOption.AllDirectories)
				: [];
		
		return projects.Select(ReadPin).OfType<SemanticVersion>().OrderDescending().Cast<SemanticVersion?>()
			.FirstOrDefault();
	}
	
	public static string? FindProjectPath(IEnumerable<string> arguments) =>
		CommandLine.FindPaths([..arguments.TakeWhile(static argument => argument != "--")]).FirstOrDefault();
	
	private static SemanticVersion? ReadPin(string project)
	{
		try
		{
			return ProjectPin.Read(File.ReadAllBytes(project));
		}
		catch (IOException)
		{
			return null;
		}
	}
}