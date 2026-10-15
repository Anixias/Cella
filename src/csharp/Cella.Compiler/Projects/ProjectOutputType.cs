namespace Cella.Compiler.Projects;

public enum ProjectOutputType
{
	Executable,
	WindowedExecutable,
	SharedLibrary,
	StaticLibrary
}

public static class ProjectOutputTypes
{
	public static bool IsExecutable(this ProjectOutputType type) =>
		type is ProjectOutputType.Executable or ProjectOutputType.WindowedExecutable;
}