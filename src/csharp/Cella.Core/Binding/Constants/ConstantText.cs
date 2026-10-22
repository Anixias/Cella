using System.Globalization;
using System.Text;
using Cella.Core.Symbols;
using Cella.Diagnostics;

namespace Cella.Core.Binding.Constants;

public static class ConstantText
{
	private const int MaxLength = 200;
	
	public static string? Describe(Constant constant) =>
		Write(constant) is { Length: <= MaxLength } text ? text : null;
	
	private static string? Write(Constant constant) => constant switch
	{
		IntegerConstant { Type: CharType } character when DescribeChar(character.Value) is { } text => text,
		IntegerConstant integer => integer.Value.ToString(CultureInfo.InvariantCulture),
		FloatConstant number => DescribeFloat(number),
		BoolConstant flag => flag.Value ? "true" : "false",
		NullConstant => "null",
		StringConstant text => Quote(text.Text),
		EnumConstant value => WriteAll(value.Payload) is { } payload
			? $"{TypeName(value.Type.OriginalDefinition)}.{value.Case.Name}{(value.Payload.IsEmpty ? "" : $"({payload})")}"
			: null,
		RecordConstant record => WriteAll(record.Fields) is { } fields ? $"{TypeName(record.Type)}({fields})" : null,
		ArrayConstant array => WriteAll(array.Elements) is { } elements ? $"[{elements}]" : null,
		ZeroConstant { Type: IntegerType } => "0",
		ZeroConstant { Type: FloatType } => "0.0",
		ZeroConstant { Type: var type } when type == NativeSymbols.Bool => "false",
		ZeroConstant { Type: PointerType } => "null",
		_ => null
	};
	
	private static string? WriteAll(IEnumerable<Constant> constants)
	{
		var parts = new List<string>();
		var length = 0;
		foreach (var constant in constants)
		{
			if (Write(constant) is not { } part || (length += part.Length + 2) > MaxLength)
				return null;
			
			parts.Add(part);
		}
		
		return string.Join(", ", parts);
	}
	
	private static string TypeName(TypeSymbol type) => DiagnosticReporter.DescribeType(type);
	
	private static string DescribeFloat(FloatConstant number)
	{
		var isSingle = number.Type == NativeSymbols.Float32;
		if (double.IsNaN(number.Value))
			return isSingle
				? $"NaN (0x{number.Bits ?? BitConverter.SingleToUInt32Bits((float)number.Value):X8})"
				: $"NaN (0x{number.Bits ?? BitConverter.DoubleToUInt64Bits(number.Value):X16})";
		
		if (double.IsInfinity(number.Value))
			return number.Value > 0 ? "infinity" : "-infinity";
		
		var text = isSingle
			? ((float)number.Value).ToString("R", CultureInfo.InvariantCulture)
			: number.Value.ToString("R", CultureInfo.InvariantCulture);
		
		return text.Contains('.') || text.Contains('E') ? text : $"{text}.0";
	}
	
	private static string? DescribeChar(System.Numerics.BigInteger value)
	{
		if (value < 0 || value > 0x10FFFF || !Rune.IsValid((int)value))
			return null;
		
		var rune = new Rune((int)value);
		return rune.Value switch
		{
			'\'' => @"'\''",
			'\\' => @"'\\'",
			'\n' => @"'\n'",
			'\t' => @"'\t'",
			'\r' => @"'\r'",
			0 => @"'\0'",
			_ when Rune.IsControl(rune) => null,
			_ => $"'{rune}'"
		};
	}
	
	private static string Quote(string text)
	{
		var quoted = new StringBuilder("\"");
		foreach (var rune in text.EnumerateRunes())
		{
			quoted.Append(rune.Value switch
			{
				'"' => "\\\"",
				'\\' => @"\\",
				'\n' => @"\n",
				'\t' => @"\t",
				'\r' => @"\r",
				0 => @"\0",
				< 0x100 when Rune.IsControl(rune) => $"\\x{rune.Value:X2}",
				_ => rune.ToString()
			});
		}
		
		return quoted.Append('"').ToString();
	}
}