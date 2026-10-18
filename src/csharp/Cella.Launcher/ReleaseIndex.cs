using System.Text.Json;
using Cella.Compiler.Projects;

namespace Cella.Launcher;

internal sealed record DownloadFile(string Url, string Sha256, long Size);
internal sealed record Component(string Version, DownloadFile File);

internal sealed record Release
(
	SemanticVersion Version,
	IReadOnlyDictionary<string, DownloadFile> Hosts,
	IReadOnlyDictionary<string, Component> Components
);

internal sealed record LauncherRelease(SemanticVersion Version, IReadOnlyDictionary<string, DownloadFile> Hosts);

internal sealed class ReleaseIndex
{
	private const string DefaultUrl = "https://anixias.github.io/Cella/index.json";
	
	public static string Url => Environment.GetEnvironmentVariable("CELLA_INDEX_URL") is { Length: > 0 } url
		? url
		: DefaultUrl;
	
	public SemanticVersion? Stable { get; private init; }
	
	public IReadOnlyDictionary<SemanticVersion, Release> Releases { get; private init; } =
		new Dictionary<SemanticVersion, Release>();
	
	public LauncherRelease? Launcher { get; private init; }
	
	public static async Task<ReleaseIndex> LoadAsync()
	{
		string text;
		try
		{
			text = Url.StartsWith("file://", StringComparison.Ordinal)
				? await File.ReadAllTextAsync(new Uri(Url).LocalPath)
				: await Downloads.Client.GetStringAsync(Url);
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
		{
			throw new LauncherException($"Couldn't read the list of releases from {Url}: {exception.Message}");
		}
		
		try
		{
			using var document = JsonDocument.Parse(text);
			return Parse(document.RootElement);
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException
			                                  or KeyNotFoundException)
		{
			throw new LauncherException($"The list of releases at {Url} is invalid: {exception.Message}");
		}
	}
	
	private static ReleaseIndex Parse(JsonElement root)
	{
		var releases = new Dictionary<SemanticVersion, Release>();
		foreach (var entry in root.GetProperty("versions").EnumerateObject())
		{
			if (SemanticVersion.Parse(entry.Name) is not { } version)
				continue;
			
			var hosts = entry.Value.GetProperty("hosts").EnumerateObject()
				.ToDictionary(static host => host.Name, static host => ParseFile(host.Value));
			
			var components = entry.Value.GetProperty("components").EnumerateObject()
				.ToDictionary(static component => component.Name, static component =>
					new Component(component.Value.GetProperty("version").GetString()!, ParseFile(component.Value)));
			
			releases[version] = new Release(version, hosts, components);
		}
		
		var stable = root.TryGetProperty("channels", out var channels) &&
		             channels.TryGetProperty("stable", out var channel) && channel.ValueKind == JsonValueKind.String
			? SemanticVersion.Parse(channel.GetString()!)
			: null;
		
		LauncherRelease? launcher = null;
		if (root.TryGetProperty("launcher", out var launcherEntry) &&
		    SemanticVersion.Parse(launcherEntry.GetProperty("version").GetString()!) is { } launcherVersion)
			launcher = new LauncherRelease(launcherVersion, launcherEntry.GetProperty("hosts").EnumerateObject()
				.ToDictionary(static host => host.Name, static host => ParseFile(host.Value)));
		
		return new ReleaseIndex { Stable = stable, Releases = releases, Launcher = launcher };
	}
	
	private static DownloadFile ParseFile(JsonElement element) => new(element.GetProperty("url").GetString()!,
		element.GetProperty("sha256").GetString()!, element.GetProperty("size").GetInt64());
}