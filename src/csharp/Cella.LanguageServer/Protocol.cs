using System.Text.Json.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.LanguageServer;

public static class Protocol
{
	private const int ErrorSeverity = 1;
	private const int WarningSeverity = 2;
	private const int HintSeverity = 4;
	private const int UnnecessaryTag = 1;
	
	public static StringComparer PathComparer { get; } =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	
	public static string ToPath(string uri)
	{
		var parsed = new Uri(uri);
		var path = Uri.UnescapeDataString(parsed.AbsolutePath);
		if (!OperatingSystem.IsWindows())
			return Path.GetFullPath(path);
		
		if (path is ['/', var drive, ':', ..] && char.IsAsciiLetter(drive))
			path = path[1..];
		
		return Path.GetFullPath(parsed.Host.Length > 0 ? $@"\\{parsed.Host}{path}" : path);
	}
	
	public static string ToUri(string path) => new Uri(path).AbsoluteUri;
	
	public static JsonObject Notification(string method, JsonNode? parameters) => new()
	{
		["jsonrpc"] = "2.0",
		["method"] = method,
		["params"] = parameters
	};
	
	public static JsonObject Request(string id, string method) => new()
	{
		["jsonrpc"] = "2.0",
		["id"] = id,
		["method"] = method
	};
	
	public static JsonObject Response(JsonNode id, JsonNode? result) => new()
	{
		["jsonrpc"] = "2.0",
		["id"] = id,
		["result"] = result
	};
	
	public static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
	{
		["jsonrpc"] = "2.0",
		["id"] = id,
		["error"] = new JsonObject { ["code"] = code, ["message"] = message }
	};
	
	public static JsonObject? ToUnnecessaryLsp(Diagnostic diagnostic) => diagnostic.UnnecessaryLocation is { } location
		? new JsonObject
		{
			["range"] = ToRange(location),
			["severity"] = HintSeverity,
			["source"] = "cella",
			["message"] = diagnostic.Message,
			["tags"] = new JsonArray(UnnecessaryTag)
		}
		: null;
	
	public static JsonObject ToLsp(Diagnostic diagnostic)
	{
		var (source, range) = diagnostic.SourceLocation;
		var start = range.IsValid ? range.Start : FindTextEnd(source);
		var end = range.IsValid ? range.End : start;
		var message = diagnostic.Hints.IsEmpty
			? diagnostic.Message
			: $"{diagnostic.Message}\n{string.Join("\n", diagnostic.Hints)}";
		
		return new JsonObject
		{
			["range"] = new JsonObject { ["start"] = ToPosition(source, start), ["end"] = ToPosition(source, end) },
			["severity"] = diagnostic.Severity switch
			{
				DiagnosticSeverity.Error => ErrorSeverity,
				DiagnosticSeverity.Warning => WarningSeverity,
				_ => HintSeverity
			},
			["source"] = "cella",
			["message"] = message
		};
	}
	
	private static int FindTextEnd(ISource source) =>
		source.GetText(new TextRange(0, source.Length)).TrimEnd().Length;
	
	public static int ToOffset(string text, int line, int character)
	{
		var offset = 0;
		for (var current = 0; current < line && offset < text.Length; current++)
		{
			var end = text.IndexOf('\n', offset);
			if (end < 0)
				return text.Length;
			
			offset = end + 1;
		}
		
		return Math.Min(offset + character, text.Length);
	}
	
	public static JsonObject ToRange(SourceLocation location) => new()
	{
		["start"] = ToPosition(location.Source, location.Range.Start),
		["end"] = ToPosition(location.Source, location.Range.End)
	};
	
	public static (int Line, int Character) ToLineCharacter(ISource source, int offset)
	{
		if (source.Length == 0)
			return (0, 0);
		
		offset = Math.Clamp(offset, 0, source.Length);
		var (line, _) = source.GetLineColumn(offset);
		var lineStart = source.GetLineRange(line).Start;
		return (Math.Max(line - 1, 0), Math.Max(offset - lineStart, 0));
	}
	
	private static JsonObject ToPosition(ISource source, int offset)
	{
		var (line, character) = ToLineCharacter(source, offset);
		return new JsonObject { ["line"] = line, ["character"] = character };
	}
}