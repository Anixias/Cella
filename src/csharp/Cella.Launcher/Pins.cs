using System.Text.RegularExpressions;
using Cella.Compiler.Projects;

namespace Cella.Launcher;

internal static partial class Pins
{
	[GeneratedRegex("""^\s*CellaVersion\s*=\s*["']([^"']*)["']""")]
	private static partial Regex PinPattern();
	
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
		arguments.TakeWhile(static argument => argument != "--")
			.Where(static argument => !argument.StartsWith('-'))
			.FirstOrDefault(static argument => File.Exists(argument) || Directory.Exists(argument));
	
	private static SemanticVersion? ReadPin(string project)
	{
		try
		{
			foreach (var line in File.ReadLines(project))
			{
				if (line.TrimStart().StartsWith('['))
					break;
				
				if (PinPattern().Match(line) is { Success: true } match)
					return SemanticVersion.Parse(match.Groups[1].Value);
			}
		}
		catch (IOException)
		{
		}
		
		return null;
	}
}