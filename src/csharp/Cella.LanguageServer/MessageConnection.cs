using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Cella.LanguageServer;

public sealed class MessageConnection(Stream input, Stream output)
{
	private const string LengthHeader = "Content-Length";
	
	private readonly Stream _input = new BufferedStream(input);
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	
	public async Task<JsonObject?> ReadAsync(CancellationToken ct)
	{
		int? length = null;
		while (await ReadHeaderLineAsync(ct) is { } line)
		{
			if (line.Length == 0)
			{
				if (length is not { } size)
					continue;
				
				var body = new byte[size];
				await _input.ReadExactlyAsync(body, ct);
				return JsonNode.Parse(body) as JsonObject;
			}
			
			var separator = line.IndexOf(':');
			if (separator > 0 && line[..separator].Trim().Equals(LengthHeader, StringComparison.OrdinalIgnoreCase))
				length = int.Parse(line[(separator + 1)..].Trim(), CultureInfo.InvariantCulture);
		}
		
		return null;
	}
	
	public async Task WriteAsync(JsonObject message, CancellationToken ct)
	{
		var body = Encoding.UTF8.GetBytes(message.ToJsonString());
		var header = Encoding.ASCII.GetBytes($"{LengthHeader}: {body.Length}\r\n\r\n");
		await _writeLock.WaitAsync(ct);
		try
		{
			await output.WriteAsync(header, ct);
			await output.WriteAsync(body, ct);
			await output.FlushAsync(ct);
		}
		finally
		{
			_writeLock.Release();
		}
	}
	
	private async Task<string?> ReadHeaderLineAsync(CancellationToken ct)
	{
		var bytes = new List<byte>();
		var buffer = new byte[1];
		while (await _input.ReadAsync(buffer, ct) > 0)
		{
			if (buffer[0] == (byte)'\n')
				return Encoding.ASCII.GetString([..bytes]).TrimEnd('\r');
			
			bytes.Add(buffer[0]);
		}
		
		return null;
	}
}