// Todo: #define FIXED_POINT_SUPPORT

using System.Globalization;
using System.Text;
using System.Collections;

namespace Cella.Analysis.Text;

public class Lexer : ILexer
{
	public IBuffer Source { get; }
	
	public Lexer(IBuffer source)
	{
		Source = source;
	}
	
	public ScanResult? ScanToken(int position)
	{
		if (position >= Source.Length)
			return null;
		
		var character = Source[position];

		if (char.IsWhiteSpace(character))
			return ScanWhiteSpace(position);

		if (char.IsDigit(character))
			return ScanNumber(position);

		if (char.IsLetter(character) || character == '_')
			return ScanIdentifier(position);

		switch (character)
		{
			case '"':
				return ScanString(position);
			
			case '\'':
				return ScanChar(position);
		}

		if (TryScanComment(position) is { } comment)
			return comment;

		return ScanOperator(position);
	}

	private ScanResult ScanString(int position)
	{
		var end = position + 1;
		var isValid = true;
		var escaped = false;
		var interpolated = false;
		var interpolationLevel = 0;
		
		while (end < Source.Length)
		{
			var character = Source[end];
			
			if (character == '"' && !escaped && interpolationLevel == 0)
			{
				end++;
				break;
			}

			if (character is '\n' or '\r')
			{
				isValid = false;
				break;
			}

			switch (character)
			{
				case '{' when !escaped:
					interpolated = true;
					interpolationLevel++;
					end++;
					continue;
				
				case '}' when interpolationLevel > 0:
					interpolationLevel--;
					end++;
					continue;
				
				case '\\':
					escaped = !escaped;
					break;
				
				default:
					escaped = false;
					break;
			}

			end++;
		}

		if (!isValid)
		{
			var invalidToken = new Token(TokenType.InvalidStringLiteral, new TextRange(position, end), Source);
			return new ScanResult(invalidToken, end);
		}

		if (!interpolated)
		{
			var value = UnescapeString(Source.GetText(new TextRange(position + 1, end - 1)), true);

			if (!value.Item2)
			{
				var invalidToken = new Token(TokenType.InvalidStringLiteral, new TextRange(position, end), Source);
				return new ScanResult(invalidToken, end);
			}

			var token = new Token(TokenType.StringLiteral, new TextRange(position, end), Source, value.Item1);

			return new ScanResult(token, end);
		}
		else
		{
			// Don't escape interpolated strings -- let the parser do that!
			var value = Source.GetText(new TextRange(position + 1, end - 1));
			var token = new Token(TokenType.InterpolatedStringLiteral, new TextRange(position, end), Source, value);
			return new ScanResult(token, end);
		}
	}

	/// <summary>
	/// Unescapes a string by replacing escape sequences with their respective literal values
	/// </summary>
	/// <param name="text">The string to unescape</param>
	/// <param name="escapeOpenBrace">Whether to allow \{ as a valid escape character</param>
	/// <returns>A tuple: (result, valid)</returns>
	public static (string, bool) UnescapeString(string text, bool escapeOpenBrace = false)
	{
		var result = new StringBuilder();
		var escaped = false;
		var valid = true;

		for (var i = 0; i < text.Length; i++)
		{
			var character = text[i];
			
			if (character == '\\')
			{
				if (escaped)
				{
					result.Append('\\');
					continue;
				}

				escaped = true;
				continue;
			}

			if (!escaped)
			{
				result.Append(character);
				continue;
			}

			escaped = false;

			switch (character)
			{
				default:
					valid = false;
					break;
				case '\'':
					result.Append('\'');
					break;
				case '"':
					result.Append('"');
					break;
				case '0':
					result.Append('\0');
					break;
				case 'a':
					result.Append('\a');
					break;
				case 'b':
					result.Append('\b');
					break;
				case 'f':
					result.Append('\f');
					break;
				case 'n':
					result.Append('\n');
					break;
				case 'r':
					result.Append('\r');
					break;
				case 't':
					result.Append('\t');
					break;
				case 'v':
					result.Append('\v');
					break;
				case '{':
					if (escapeOpenBrace)
						result.Append('{');
					else
						valid = false;
					break;
				case 'u':
					const int utf16Length = 4;
					if (i + utf16Length >= text.Length)
					{
						valid = false;
						i += utf16Length;
					}
					else
					{
						var sequence = text.Substring(i + 1, utf16Length);
						i += utf16Length;

						var sequenceValue = uint.Parse(sequence, NumberStyles.AllowHexSpecifier);
						var chars = ConvertUnicodeCodePointToUTF8(sequenceValue);
						var str = Encoding.UTF8.GetString(chars);
						result.Append(str);
					}

					break;
				case 'U':
					// Todo: Test UTF32
					const int utf32Length = 8;
					if (i + utf32Length >= text.Length)
					{
						valid = false;
						i += utf32Length;
					}
					else
					{
						var sequence = text.Substring(i + 1, utf32Length);
						i += utf32Length;

						var sequenceValue = uint.Parse(sequence, NumberStyles.AllowHexSpecifier);
						result.Append(Encoding.UTF32.GetString(BitConverter.GetBytes(sequenceValue)));
					}

					break;
			}
		}

		return (result.ToString(), valid);
	}

	private static byte[] ConvertUnicodeCodePointToUTF8(uint codePoint)
	{
		return codePoint switch
		{
			<= 0x7Fu => 
			[
				(byte)codePoint
			],
			
			<= 0x7FFu => 
			[
				(byte)(0xC0 | (codePoint >> 6)),
				(byte)(0x80 | (codePoint & 0x3F))
			],
			
			<= 0xFFFFu =>
			[
				(byte)(0xE0 | (codePoint >> 12)), (byte)(0x80 | ((codePoint >> 6) & 0x3F)),
				(byte)(0x80 | (codePoint & 0x3F))
			],
			
			<= 0x10FFFFu =>
			[
				(byte)(0xF0 | (codePoint >> 18)), (byte)(0x80 | ((codePoint >> 12) & 0x3F)),
				(byte)(0x80 | ((codePoint >> 6) & 0x3F)), (byte)(0x80 | (codePoint & 0x3F))
			],
			
			_ => Array.Empty<byte>()
		};
	}

	private ScanResult ScanChar(int position)
	{
		var end = position + 1;
		var isValid = true;
		var escaped = false;
		
		while (end < Source.Length)
		{
			if (Source[end] == '\'' && !escaped)
			{
				end++;
				break;
			}

			if (Source[end] is '\n' or '\r')
			{
				isValid = false;
				break;
			}

			if (Source[end] == '\\')
				escaped = !escaped;
			else
				escaped = false;

			end++;
		}
		
		if (!isValid)
		{
			var invalidToken = new Token(TokenType.InvalidCharLiteral, new TextRange(position, end), Source);
			return new ScanResult(invalidToken, end);
		}

		var value = UnescapeString(Source.GetText(new TextRange(position + 1, end - 1)));
		if (value.Item1.Length != 1 || !value.Item2)
		{
			var invalidToken = new Token(TokenType.InvalidCharLiteral, new TextRange(position, end), Source);
			return new ScanResult(invalidToken, end);
		}

		var bytes = Encoding.UTF8.GetBytes(value.Item1);
		var paddedBytes = new byte[4];
		Array.Copy(bytes, paddedBytes, bytes.Length);
		
		var token = new Token(TokenType.CharLiteral, new TextRange(position, end), Source, paddedBytes);
		return new ScanResult(token, end);
	}

	private ScanResult? TryScanComment(int position)
	{
		if (Source[position] != '/')
			return null;
		
		if (position + 1 >= Source.Length)
			return null;

		return Source[position + 1] switch
		{
			'/' => ScanLineComment(position),
			'*' => ScanMultilineComment(position),
			_ => null
		};
	}

	private ScanResult ScanLineComment(int position)
	{
		var end = position + 2;
		while (end < Source.Length)
		{
			var character = Source[end];

			if (character is '\n' or '\r')
				break;
			
			end++;
		}

		return new ScanResult(new Token(TokenType.LineComment, new TextRange(position, end), Source), end);
	}

	private ScanResult ScanMultilineComment(int position)
	{
		var end = position + 2;
		var nestLevel = 1;
		while (end < Source.Length)
		{
			if (end + 1 >= Source.Length)
			{
				end++;
				break;
			}

			var character = Source[end];
			var next = Source[end + 1];

			if (character == '/' && next == '*')
			{
				nestLevel++;
				end += 2;
				continue;
			}
			
			if (character == '*' && next == '/')
			{
				nestLevel--;
				end += 2;
				
				if (nestLevel <= 0)
					break;
				
				continue;
			}

			end++;
		}

		return new ScanResult(new Token(TokenType.MultilineComment, new TextRange(position, end), Source), end);
	}

	private List<Token> ScanAllTokens()
	{
		var tokens = new List<Token>();
		var position = 0;
		
		while (true)
		{
			if (ScanToken(position) is not { } lexerResult)
			{
				return tokens;
			}

			tokens.Add(lexerResult.Token);
			position = lexerResult.NextPosition;
		}
	}
	
	private int? ScanStorageSize(int position)
	{
		var end = position;
		while (end < Source.Length && char.IsDigit(Source[end]))
			end++;

		if (end <= position)
			return null;

		return int.Parse(Source.GetText(new TextRange(position, end)));
	}

	private ScanResult ScanOperator(int position)
	{
		var end = position;
		while (end < Source.Length)
		{
			var character = Source[end];
			if (char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || character == '_')
				break;
			
			end++;
		}

		while (end > position)
		{
			var range = new TextRange(position, end);
			var operatorString = Source.GetText(range);
			if (TokenType.GetOperator(operatorString) is { } @operator)
			{
				var token = new Token(@operator, range, Source);
				return new ScanResult(token, end);
			}
			
			end--;
		}

		end = position + 1;
		return new ScanResult(new Token(TokenType.Invalid, new TextRange(position, end), Source), end);
	}

	private ScanResult ScanWhiteSpace(int position)
	{
		if (Source[position] is '\n' or '\r')
			return ScanNewline(position);
		
		var end = position;
		while (end < Source.Length && char.IsWhiteSpace(Source[end]))
			end++;

		var range = new TextRange(position, end);
		var token = new Token(TokenType.Whitespace, range, Source);
		return new ScanResult(token, end);
	}

	private ScanResult ScanNewline(int position)
	{
		var end = position;
		if (Source[end] == '\r')
			end++;
		
		if (Source[end] == '\n')
			end++;
		
		var range = new TextRange(position, end);
		var token = new Token(TokenType.Newline, range, Source);
		return new ScanResult(token, end);
	}
	
	private ScanResult ScanIdentifier(int position)
	{
		var end = position;
		while (end < Source.Length && (char.IsLetterOrDigit(Source[end]) || Source[end] == '_'))
			end++;

		var range = new TextRange(position, end);
		var text = Source.GetText(range);
		var tokenType = TokenType.GetKeyword(text) ?? TokenType.Identifier;
		var token = new Token(tokenType, range, Source);
		return new ScanResult(token, end);
	}

	private ScanResult ScanNumber(int position)
	{
		// 1_234, 123.456, 123.456f32, 123.456x32, 123u, 8i64, 0xAF80, 0b1011_0011, 2e3, 2e-3, 3.14e+3f
		var end = position;

		if (position <= Source.Length - 2)
		{
			if (Source[position] == '0')
			{
				switch (Source[position + 1])
				{
					case 'x':
					case 'X':
						return ScanHexadecimal(position);
					case 'b':
					case 'B':
						return ScanBinary(position);
				}
			}
		}

		object? value = null;
		
		while (end < Source.Length && IsDigit(Source[end]))
		{
			end++;
		}

		if (end < Source.Length)
		{
			if (Source[end] == '.' && end + 1 < Source.Length && IsDigit(Source[end + 1]))
			{
				end++;
				while (end < Source.Length && IsDigit(Source[end]))
				{
					end++;
				}

				var valueString = RemoveSeparators(Source.GetText(new TextRange(position, end)));

				if (end < Source.Length)
				{
					switch (Source[end])
					{
						case 'e':
						case 'E':
							end++;
							if (end < Source.Length && Source[end] is '+' or '-')
								end++;

							while (end < Source.Length && IsDigit(Source[end]))
								end++;

							valueString = RemoveSeparators(Source.GetText(new TextRange(position, end)));
							end = ParseFloatSuffix(end, valueString, out value);
							break;
						
						case 'f':
						case 'F':
							end = ParseFloatSuffix(end, valueString, out value);
							break;
						
#if FIXED_POINT_SUPPORT
						case 'x':
						case 'X':
							end = ParseFixedSuffix(end, valueString, out value);
							break;
#endif
						
						// Todo: Invalid suffix
						default:
							value = double.TryParse(valueString, out var defaultValue) ? defaultValue : null;
							break;
					}
				}
				else
				{
					value = double.TryParse(valueString, out var doubleValue) ? doubleValue : null;
				}
			}
			else
			{
				var valueString = RemoveSeparators(Source.GetText(new TextRange(position, end)));

				if (end < Source.Length)
				{
					switch (Source[end])
					{
						case 'u':
						case 'U':
							end++;
							switch (ScanStorageSize(end))
							{
								case 8:
									end++;
									value = byte.TryParse(valueString, out var byteValue) ? byteValue : null;
									break;
								
								case 16:
									end += 2;
									value = ushort.TryParse(valueString, out var ushortValue) ? ushortValue : null;
									break;
								
								case 32:
									end += 2;
									value = uint.TryParse(valueString, out var uintValue) ? uintValue : null;
									break;
								
								case 64:
									end += 2;
									value = ulong.TryParse(valueString, out var ulongValue) ? ulongValue : null;
									break;
								
								case 128:
									end += 3;
									value = UInt128.TryParse(valueString, out var uint128Value) ? uint128Value : null;
									break;
								
								// Todo: Invalid suffix
								case null:
									value = uint.TryParse(valueString, out var uDefaultValue) ? uDefaultValue : null;
									break;
							}
							break;
						case 'i':
						case 'I':
							end++;
							switch (ScanStorageSize(end))
							{
								case 8:
									end++;
									value = sbyte.TryParse(valueString, out var sbyteValue) ? sbyteValue : null;
									break;
								
								case 16:
									end += 2;
									value = short.TryParse(valueString, out var shortValue) ? shortValue : null;
									break;
								
								case 32:
									end += 2;
									value = int.TryParse(valueString, out var sIntValue) ? sIntValue : null;
									break;
								
								case 64:
									end += 2;
									value = long.TryParse(valueString, out var longValue) ? longValue : null;
									break;
								
								case 128:
									end += 3;
									value = Int128.TryParse(valueString, out var int128Value) ? int128Value : null;
									break;
								
								// Todo: Invalid suffix
								case null:
									value = int.TryParse(valueString, out var iDefaultValue) ? iDefaultValue : null;
									break;
							}
							break;
						case 'e':
						case 'E':
							end++;
							if (end < Source.Length && Source[end] is '+' or '-')
								end++;

							while (end < Source.Length && char.IsDigit(Source[end]))
								end++;

							valueString = Source.GetText(new TextRange(position, end));
							end = ParseFloatSuffix(end, valueString, out value);
							break;
						
						case 'f':
						case 'F':
							end = ParseFloatSuffix(end, valueString, out value);
							break;
						
#if FIXED_POINT_SUPPORT
						case 'x':
						case 'X':
							end = ParseFixedSuffix(end, valueString, out value);
							break;
#endif
						
						// Todo: Invalid suffix
						default:
							if (int.TryParse(valueString, out var intValue))
							{
								value = intValue;
							}
							else if (long.TryParse(valueString, out var longValue))
							{
								value = longValue;
							}
							else if (Int128.TryParse(valueString, out var int128Value))
							{
								value = int128Value;
							}
							else
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
					}
				}
				else
				{
					if (int.TryParse(valueString, out var intValue))
					{
						value = intValue;
					}
					else if (long.TryParse(valueString, out var longValue))
					{
						value = longValue;
					}
					else if (Int128.TryParse(valueString, out var int128Value))
					{
						value = int128Value;
					}
					else
					{
						var invalidToken = new Token(TokenType.InvalidNumberLiteral, new TextRange(position, end),
							Source, value);
						return new ScanResult(invalidToken, end);
					}
				}
			}
		}
		else
		{
			var valueString = RemoveSeparators(Source.GetText(new TextRange(position, end)));
			if (int.TryParse(valueString, out var intValue))
			{
				value = intValue;
			}
			else if (uint.TryParse(valueString, out var uintValue))
			{
				value = uintValue;
			}
			else if (long.TryParse(valueString, out var longValue))
			{
				value = longValue;
			}
			else if (ulong.TryParse(valueString, out var ulongValue))
			{
				value = ulongValue;
			}
			else if (Int128.TryParse(valueString, out var int128Value))
			{
				value = int128Value;
			}
			else if (UInt128.TryParse(valueString, out var uint128Value))
			{
				value = uint128Value;
			}
			else
			{
				var invalidToken = new Token(TokenType.InvalidNumberLiteral, new TextRange(position, end), Source,
					value);
				return new ScanResult(invalidToken, end);
			}
		}

		var token = new Token(TokenType.NumberLiteral, new TextRange(position, end), Source, value);
		return new ScanResult(token, end);

		bool IsDigit(char c)
		{
			return char.IsDigit(c) || c == '_';
		}

		string RemoveSeparators(string str)
		{
			return str.Replace("_", null);
		}
	}

	private int ParseFloatSuffix(int end, string valueString, out object? value)
	{
		value = null;
		
		if (end < Source.Length)
		{
			if (Source[end] is not ('f' or 'F'))
				return end;
			
			end++;
			switch (ScanStorageSize(end))
			{
				case 32:
					end += 2;

					value = float.TryParse(valueString, NumberStyles.Float, null,
						out var float32Value)
						? float32Value
						: null;
					break;
				
				case 64:
					end += 2;

					value = double.TryParse(valueString, NumberStyles.Float, null,
						out var float64Value)
						? float64Value
						: null;
					break;
				
				case 128:
					end += 3;

					value = decimal.TryParse(valueString, NumberStyles.Float, null,
						out var float128Value)
						? float128Value
						: null;
					break;
				
				// Todo: Invalid suffix
				case null:
					value = float.TryParse(valueString, NumberStyles.Float, null,
						out var floatValue)
						? floatValue
						: null;
					break;
			}
		}
		else
		{
			value = double.TryParse(valueString, NumberStyles.Float, null,
				out var doubleValue)
				? doubleValue
				: null;
		}

		return end;
	}
	
#if FIXED_POINT_SUPPORT
	private int ParseFixedSuffix(int end, string valueString, out object? value)
	{
		value = null;

		if (end >= Source.Length)
			return end;
		
		if (Source[end] is not ('x' or 'X'))
			return end;
			
		end++;
		switch (ScanStorageSize(end))
		{
			case 32:
				end += 2;

				value = Fixed32.TryParse(valueString, out var fixed32Value)
					? fixed32Value
					: null;
				break;
			
			case 64:
				end += 2;

				value = Fixed64.TryParse(valueString, out var fixed64Value)
					? fixed64Value
					: null;
				break;
			
			case 128:
				end += 3;

				value = Fixed128.TryParse(valueString, out var fixed128Value)
					? fixed128Value
					: null;
				break;
			
			// Todo: Invalid suffix
			case null:
				value = Fixed64.TryParse(valueString, out var fixedValue)
					? fixedValue
					: null;
				break;
		}

		return end;
	}
#endif

	private ScanResult ScanHexadecimal(int position)
	{
		var end = position + 2;
		while (end < Source.Length && IsHexDigit(Source[end]))
		{
			end++;
		}

		var valueString = RemoveSeparators(Source.GetText(new TextRange(position + 2, end)));
		object? value = null;
		
		var scannedSuffix = false;
		if (end < Source.Length)
		{
			switch (Source[end])
			{
				case 'u':
				case 'U':
					end++;
					scannedSuffix = true;
					switch (ScanStorageSize(end))
					{
						case 8:
							end++;
							if (byte.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var byteValue))
								value = byteValue;
							break;
						
						case 16:
							end += 2;
							if (ushort.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var ushortValue))
								value = ushortValue;
							break;
						
						case 32:
							end += 2;
							if (uint.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var uintValue))
								value = uintValue;
							break;
						
						case 64:
							end += 2;
							if (ulong.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var ulongValue))
								value = ulongValue;
							break;
						
						case 128:
							end += 3;
							if (UInt128.TryParse(valueString, NumberStyles.AllowHexSpecifier, null,
								    out var uint128Value))
								value = uint128Value;
							break;
						
						// Todo: Invalid suffix
						case null:
							if (uint.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var uDefaultValue))
								value = uDefaultValue;
							break;
					}

					break;
				case 'i':
				case 'I':
					end++;
					scannedSuffix = true;
					switch (ScanStorageSize(end))
					{
						case 8:
							end++;
							if (sbyte.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var sbyteValue))
								value = sbyteValue;
							break;
						
						case 16:
							end += 2;
							if (short.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var shortValue))
								value = shortValue;
							break;
						
						case 32:
							end += 2;
							if (int.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var sIntValue))
								value = sIntValue;
							break;
						
						case 64:
							end += 2;
							if (long.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var longValue))
								value = longValue;
							break;
						
						case 128:
							end += 3;
							if (Int128.TryParse(valueString, NumberStyles.AllowHexSpecifier, null,
								    out var int128Value))
								value = int128Value;
							break;
						
						// Todo: Invalid suffix
						case null:
							if (int.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var iDefaultValue))
								value = iDefaultValue;
							break;
					}
					break;
			}
		}

		if (!scannedSuffix)
		{
			if (int.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var intValue))
			{
				value = intValue;
			}
			else if (uint.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var uintValue))
			{
				value = uintValue;
			}
			else if (long.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var longValue))
			{
				value = longValue;
			}
			else if (ulong.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var ulongValue))
			{
				value = ulongValue;
			}
			else if (Int128.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var int128Value))
			{
				value = int128Value;
			}
			else if (UInt128.TryParse(valueString, NumberStyles.AllowHexSpecifier, null, out var uint128Value))
			{
				value = uint128Value;
			}
			else
			{
				var invalidToken = new Token(TokenType.InvalidNumberLiteral, new TextRange(position, end), Source,
					value);
				return new ScanResult(invalidToken, end);
			}
		}

		var token = new Token(TokenType.NumberLiteral, new TextRange(position, end), Source, value);
		return new ScanResult(token, end);

		bool IsHexDigit(char c)
		{
			return char.IsAsciiHexDigit(c) || c == '_';
		}

		string RemoveSeparators(string str)
		{
			return str.Replace("_", null);
		}
	}

	private ScanResult ScanBinary(int position)
	{
		var end = position + 2;
		while (end < Source.Length && Source[end] is '0' or '1' or '_')
		{
			end++;
		}

		var valueString = RemoveSeparators(Source.GetText(new TextRange(position + 2, end)));
		object? value = null;

		var scannedSuffix = false;
		if (end < Source.Length)
		{
			switch (Source[end])
			{
				case 'u':
				case 'U':
					end++;
					scannedSuffix = true;
					switch (ScanStorageSize(end))
					{
						case 8:
							end++;
							try
							{
								value = Convert.ToByte(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						case 16:
							end += 2;
							try
							{
								value = Convert.ToUInt16(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						case 32:
							end += 2;
							try
							{
								value = Convert.ToUInt32(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						case 64:
							end += 2;
							try
							{
								value = Convert.ToUInt64(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						case 128:
							end += 3;
							if (UInt128.TryParse(valueString, NumberStyles.AllowBinarySpecifier, null,
								    out var uint128Value))
							{
								value = uint128Value;
							}
							else
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						// Todo: Invalid suffix
						case null:
							try
							{
								value = Convert.ToUInt32(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
					}

					break;
				case 'i':
				case 'I':
					end++;
					scannedSuffix = true;
					switch (ScanStorageSize(end))
					{
						case 8:
							end++;
							try
							{
								value = Convert.ToSByte(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						case 16:
							end += 2;
							try
							{
								value = Convert.ToInt16(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						case 32:
							end += 2;
							try
							{
								value = Convert.ToInt32(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						case 64:
							end += 2;
							try
							{
								value = Convert.ToInt64(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						case 128:
							end += 3;
							if (Int128.TryParse(valueString, NumberStyles.AllowBinarySpecifier, null,
								    out var int128Value))
							{
								value = int128Value;
							}
							else
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
						
						// Todo: Invalid suffix
						case null:
							try
							{
								value = Convert.ToInt32(valueString, 2);
							}
							catch (OverflowException)
							{
								var invalidToken = new Token(TokenType.InvalidNumberLiteral,
									new TextRange(position, end), Source, value);
								return new ScanResult(invalidToken, end);
							}
							break;
					}
					break;
			}
		}

		if (!scannedSuffix)
		{
			try
			{
				value = Convert.ToInt32(valueString, 2);
			}
			catch (OverflowException)
			{
				try
				{
					value = Convert.ToUInt32(valueString, 2);
				}
				catch (OverflowException)
				{
					try
					{
						value = Convert.ToInt64(valueString, 2);
					}
					catch (OverflowException)
					{
						try
						{
							value = Convert.ToUInt64(valueString, 2);
						}
						catch (OverflowException)
						{
							if (Int128.TryParse(valueString, NumberStyles.AllowBinarySpecifier, null,
								    out var int128Value))
							{
								value = int128Value;
							}
							else
							{
								if (UInt128.TryParse(valueString, NumberStyles.AllowBinarySpecifier, null,
									    out var uint128Value))
								{
									value = uint128Value;
								}
								else
								{
									var invalidToken = new Token(TokenType.InvalidNumberLiteral,
										new TextRange(position, end), Source, value);
									return new ScanResult(invalidToken, end);
								}
							}
						}
					}
				}
			}
		}

		var token = new Token(TokenType.NumberLiteral, new TextRange(position, end), Source, value);
		return new ScanResult(token, end);

		string RemoveSeparators(string str)
		{
			return str.Replace("_", null);
		}
	}

	public IEnumerator<Token> GetEnumerator()
	{
		return ScanAllTokens().GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
}