namespace Cella.Compiler.Projects;

public static class ProjectSearch
{
	private const string ProjectSearchPattern = "*.celp";
	
	public static IEnumerable<string> FindProjects(string directory)
	{
		var pending = new Queue<string>([directory]);
		while (pending.TryDequeue(out var next))
		{
			foreach (var project in Directory.EnumerateFiles(next, ProjectSearchPattern))
				yield return project;
			
			foreach (var subdirectory in EnumerateSubdirectories(next))
				pending.Enqueue(subdirectory);
		}
	}
	
	public static IEnumerable<string> EnumerateSubdirectories(string directory) => new DirectoryInfo(directory)
		.EnumerateDirectories()
		.Where(static subdirectory => !subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
		                              subdirectory.LinkTarget is null)
		.Select(subdirectory => Path.Join(directory, subdirectory.Name));
}
