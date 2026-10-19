namespace Cella.Core.Text;

public static class SourcePaths
{
	public static string Relative(string root, string path) => string.Join('/',
		Path.GetRelativePath(root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]));
}