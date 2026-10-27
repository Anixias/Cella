using System.Text;

namespace Cella.Compiler.Linking;

public static class ResponseFile
{
	private static readonly char[] _windowsSpecial = [' ', '\t', '\r', '\n', '"'];
	private static readonly char[] _gnuSpecial = [' ', '\t', '\r', '\n', '"', '\'', '\\'];
	
	public static Task WriteAsync(string path, IEnumerable<string> arguments, ResponseFileQuoting quoting) =>
		File.WriteAllLinesAsync(path, arguments.Select(argument => Quote(argument, quoting)));
	
	public static string Quote(string argument, ResponseFileQuoting quoting) => quoting switch
	{
		ResponseFileQuoting.Gnu when argument.Length > 0 && argument.IndexOfAny(_gnuSpecial) < 0 => argument,
		ResponseFileQuoting.Gnu => $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
		_ when argument.Length > 0 && argument.IndexOfAny(_windowsSpecial) < 0 => argument,
		_ => QuoteForWindows(argument)
	};
	
	private static string QuoteForWindows(string argument)
	{
		var quoted = new StringBuilder("\"");
		var backslashes = 0;
		foreach (var character in argument)
		{
			if (character == '\\')
			{
				backslashes++;
				continue;
			}
			
			quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes).Append(character);
			backslashes = 0;
		}
		
		return quoted.Append('\\', backslashes * 2).Append('"').ToString();
	}
}