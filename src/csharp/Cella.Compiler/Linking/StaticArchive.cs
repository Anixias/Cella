using System.Buffers.Binary;
using System.Text;

namespace Cella.Compiler.Linking;

public static class StaticArchive
{
	private const int HeaderSize = 60;
	private const int SizeOffset = 48;
	private const int SizeLength = 10;
	
	private static readonly byte[] _signature = "!<arch>\n"u8.ToArray();
	private static readonly byte[] _importObject = [0x00, 0x00, 0xFF, 0xFF];
	
	public static IReadOnlySet<string> ReadObjectDefinitions(string path)
	{
		var data = File.ReadAllBytes(path);
		if (!data.AsSpan().StartsWith(_signature) || data.Length < _signature.Length + HeaderSize)
			return new HashSet<string>();
		
		var header = data.AsSpan(_signature.Length, HeaderSize);
		if (header[0] != (byte)'/' || header[1] != (byte)' ' ||
		    !int.TryParse(Encoding.ASCII.GetString(header.Slice(SizeOffset, SizeLength)).Trim(), out var size))
			return new HashSet<string>();
		
		var table = data.AsSpan(_signature.Length + HeaderSize,
			Math.Min(size, data.Length - _signature.Length - HeaderSize));
		
		if (table.Length < 4)
			return new HashSet<string>();
		
		var count = (int)BinaryPrimitives.ReadUInt32BigEndian(table);
		var names = 4 + count * 4;
		if (count < 0 || names > table.Length)
			return new HashSet<string>();
		
		var definitions = new HashSet<string>(StringComparer.Ordinal);
		var position = names;
		for (var i = 0; i < count && position < table.Length; i++)
		{
			var length = table[position..].IndexOf((byte)0);
			if (length < 0)
				break;
			
			var name = Encoding.UTF8.GetString(table.Slice(position, length));
			position += length + 1;
			var member = (long)BinaryPrimitives.ReadUInt32BigEndian(table[(4 + i * 4)..]) + HeaderSize;
			if (member + _importObject.Length <= data.Length &&
			    !data.AsSpan((int)member, _importObject.Length).SequenceEqual(_importObject))
				definitions.Add(name);
		}
		
		return definitions;
	}
}