using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Cella.Compiler.Projects;

namespace Cella.Launcher;

internal static class Downloads
{
	public static HttpClient Client { get; } = CreateClient();
	
	private static HttpClient CreateClient()
	{
		var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("cella",
			SemanticVersion.Current.ToString()));
		
		return client;
	}
	
	public static async Task InstallAsync(DownloadFile file, string directory, string label)
	{
		var downloads = Path.Combine(Home.Directory, ".downloads");
		Directory.CreateDirectory(downloads);
		var archive = Path.Combine(downloads, Path.GetFileName(new Uri(file.Url).LocalPath));
		Console.WriteLine($"Downloading {label} ({FormatSize(file.Size)})");
		await DownloadAsync(file, archive);
		
		var partial = directory + ".partial";
		if (Directory.Exists(partial))
			Directory.Delete(partial, true);
		
		try
		{
			Extract(archive, partial);
			Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
			Directory.Move(partial, directory);
		}
		finally
		{
			if (Directory.Exists(partial))
				Directory.Delete(partial, true);
			
			File.Delete(archive);
			if (!Directory.EnumerateFileSystemEntries(downloads).Any())
				Directory.Delete(downloads);
		}
	}
	
	public static async Task DownloadAsync(DownloadFile file, string destination)
	{
		var name = Path.GetFileName(new Uri(file.Url).LocalPath);
		try
		{
			await using (var output = File.Create(destination))
			{
				await using var input = await OpenAsync(file.Url);
				await input.CopyToAsync(output);
			}
			
			await using var written = File.OpenRead(destination);
			var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(written));
			if (written.Length != file.Size || !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
				throw new LauncherException($"'{name}' doesn't match its published checksum");
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
		{
			File.Delete(destination);
			throw new LauncherException($"Couldn't download {file.Url}: {exception.Message}");
		}
		catch (LauncherException)
		{
			File.Delete(destination);
			throw;
		}
	}
	
	public static async Task<Stream> OpenAsync(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			throw new LauncherException($"'{url}' is not a valid URL");
		
		if (uri.IsFile)
			return File.OpenRead(uri.LocalPath);
		
		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			throw new LauncherException($"Can't download from '{url}'");
		
		var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStreamAsync();
	}
	
	private static void Extract(string archive, string directory)
	{
		Directory.CreateDirectory(directory);
		if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			ZipFile.ExtractToDirectory(archive, directory);
			return;
		}
		
		using var compressed = File.OpenRead(archive);
		using var tar = new GZipStream(compressed, CompressionMode.Decompress);
		TarFile.ExtractToDirectory(tar, directory, false);
	}
	
	private static string FormatSize(long bytes) => bytes >= 1024 * 1024
		? $"{bytes / (1024.0 * 1024.0):0.#} MB"
		: $"{bytes / 1024.0:0.#} KB";
}