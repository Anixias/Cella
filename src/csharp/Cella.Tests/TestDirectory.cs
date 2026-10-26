namespace Cella.Tests;

internal static class TestDirectory
{
	private static readonly string _root = Path.Combine(Path.GetTempPath(), "cella-tests");

	public static string Create(string kind) =>
		Directory.CreateDirectory(Path.Combine(_root, kind, Guid.NewGuid().ToString("N"))).FullName;
}
