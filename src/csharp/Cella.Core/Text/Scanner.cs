using System.Buffers;
using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Cella.Core.Text;

public class Scanner
{
	public ISource Source { get; }
	
	public Scanner(ISource source)
	{
		Source = source;
	}
	
	public ScanResult ScanToken(int position)
	{
		if (position < 0 || position >= Source.Length)
			return ScanResult.EndOfFile(Source);
		
		var character = Source[position];
		
		if (char.IsWhiteSpace(character))
			return ScanWhiteSpace(position);
		
		if (char.IsAsciiDigit(character))
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
		
		if (TryScanComment(position, out var comment))
			return comment;
		
		if (TryScanOperator(position, out var op))
			return op;
		
		var end = char.IsHighSurrogate(character) && position + 1 < Source.Length &&
		          char.IsLowSurrogate(Source[position + 1])
			? position + 2
			: position + 1;
		
		var range = new TextRange(position, end);
		var text = Source.GetText(range).ToString();
		var token = new Token(TokenType.Invalid, Source, range)
		{
			Error = $"Unexpected character {DescribeCharacter(text)}"
		};
		
		return new ScanResult(token, end);
	}
	
	private static string DescribeCharacter(string text)
	{
		var codePoint = char.IsSurrogatePair(text, 0) ? char.ConvertToUtf32(text, 0) : text[0];
		var isPrintable = text.Length == 1 && CharUnicodeInfo.GetUnicodeCategory(text[0]) is not
			(UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
			UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.SpaceSeparator or
			UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
		
		return isPrintable ? $"'{text}'" : $"U+{codePoint:X4}";
	}
	
	private ScanResult ScanWhiteSpace(int position)
	{
		if (Source[position] is '\n' or '\r')
			return ScanNewline(position);
		
		var end = position;
		while (end < Source.Length)
		{
			var c = Source[end];
			if (c is '\n' or '\r' || !char.IsWhiteSpace(c))
				break;
			
			end++;
		}
		
		var range = new TextRange(position, end);
		var token = new Token(TokenType.Whitespace, Source, range);
		return new ScanResult(token, end);
	}
	
	private ScanResult ScanNewline(int position)
	{
		var end = position;
		
		if (Source[end] == '\r')
			end++;
		
		if (end < Source.Length && Source[end] == '\n')
			end++;
		
		var range = new TextRange(position, end);
		var token = new Token(TokenType.Newline, Source, range);
		return new ScanResult(token, end);
	}
	
	private ScanResult ScanIdentifier(int position)
	{
		int end;
		for (end = position + 1; end < Source.Length; end++)
		{
			var c = Source[end];
			if (!char.IsLetterOrDigit(c) && c != '_')
				break;
		}
		
		var range = new TextRange(position, end);
		var text = new string(Source.GetText(range));
		var tokenType = TokenType.GetKeyword(text) ?? TokenType.Identifier;
		
		var token = new Token(tokenType, Source, range);
		return new ScanResult(token, end);
	}
	
	private ScanResult ScanNumber(int position)
	{
		// @TODO Octal literals
		if (Source[position] == '0' && position + 1 < Source.Length && Source[position + 1] is 'x' or 'X' or 'b' or 'B')
			return ScanPrefixedNumber(position);
		
		var end = ScanDigits(position + 1);
		var tokenType = TokenType.IntegerLiteral;
		
		if (end + 1 < Source.Length && Source[end] == '.' && char.IsAsciiDigit(Source[end + 1]))
		{
			end = ScanDigits(end + 1);
			tokenType = TokenType.FloatLiteral;
		}
		
		if (end < Source.Length && Source[end] is 'e' or 'E')
		{
			var exponent = end + 1;
			if (exponent < Source.Length && Source[exponent] is '+' or '-')
				exponent++;
			
			if (exponent >= Source.Length || !char.IsAsciiDigit(Source[exponent]))
			{
				var invalid = new Token(TokenType.Invalid, Source, new TextRange(position, exponent))
				{
					Error = "Expected exponent digits"
				};
				
				return new ScanResult(invalid, exponent);
			}
			
			end = ScanDigits(exponent);
			tokenType = TokenType.FloatLiteral;
		}
		
		return CreateSuffixedToken(tokenType, position, end, ScanSuffix(end));
	}
	
	private int ScanDigits(int position)
	{
		while (position < Source.Length && (char.IsAsciiDigit(Source[position]) || Source[position] == '_'))
			position++;
		
		return position;
	}
	
	private ScanResult ScanPrefixedNumber(int position)
	{
		var isHex = Source[position + 1] is 'x' or 'X';
		var end = position + 2;
		while (end < Source.Length && (char.IsLetterOrDigit(Source[end]) || Source[end] == '_'))
			end++;
		
		var suffix = Source.GetText(new TextRange(position + 2, end)).IndexOfAny('i', 'u');
		var digitsEnd = suffix < 0 ? end : position + 2 + suffix;
		if (FindPrefixedNumberError(Source.GetText(new TextRange(position + 2, digitsEnd)), isHex) is not { } error)
			return CreateSuffixedToken(TokenType.IntegerLiteral, position, digitsEnd, end);
		
		var invalid = new Token(TokenType.Invalid, Source, new TextRange(position, end)) { Error = error };
		return new ScanResult(invalid, end);
	}
	
	private int ScanSuffix(int position)
	{
		if (position >= Source.Length || !char.IsLetter(Source[position]))
			return position;
		
		var end = position + 1;
		while (end < Source.Length && (char.IsLetterOrDigit(Source[end]) || Source[end] == '_'))
			end++;
		
		return end;
	}
	
	private ScanResult CreateSuffixedToken(TokenType type, int start, int valueEnd, int end, string? text = null)
	{
		var token = new Token(type, Source, new TextRange(start, end),
			text ?? new string(Source.GetText(new TextRange(start, valueEnd))))
		{
			Suffix = end > valueEnd ? new string(Source.GetText(new TextRange(valueEnd, end))) : null
		};
		
		return new ScanResult(token, end);
	}
	
	private static string? FindPrefixedNumberError(ReadOnlySpan<char> digits, bool isHex)
	{
		var kind = isHex ? "hex" : "binary";
		foreach (var digit in digits)
		{
			if (digit != '_' && !(isHex ? char.IsAsciiHexDigit(digit) : digit is '0' or '1'))
				return $"Invalid digit '{digit}' in {kind} literal";
		}
		
		return digits.IndexOfAnyExcept('_') < 0 ? $"Expected {kind} digits" : null;
	}
	
	public static bool TryParseInteger(ReadOnlySpan<char> text, out BigInteger value)
	{
		var digits = text.ToString().Replace("_", "");
		var isNegative = digits.StartsWith('-');
		if (isNegative)
			digits = digits[1..];
		
		var (body, style) = digits switch
		{
			['0', 'x' or 'X', ..] => ("0" + digits[2..], NumberStyles.AllowHexSpecifier),
			['0', 'b' or 'B', ..] => ("0" + digits[2..], NumberStyles.AllowBinarySpecifier),
			_ => (digits, NumberStyles.None)
		};
		
		if (!BigInteger.TryParse(body, style, CultureInfo.InvariantCulture, out value))
			return false;
		
		if (isNegative)
			value = -value;
		
		return true;
	}
	
	private bool TryScanComment(int position, out ScanResult comment)
	{
		var end = position;
		if (end >= Source.Length - 1 || Source[end++] is not '/')
		{
			comment = default;
			return false;
		}
		
		TokenType tokenType;
		switch (Source[end++])
		{
			// Line comment
			case '/':
				tokenType = TokenType.LineComment;
				while (end < Source.Length && Source[end] is not '\r' and not '\n')
					end++;
				
				break;
			
			// Block comment
			case '*':
				tokenType = TokenType.BlockComment;
				var depth = 1;
				while (end < Source.Length && depth > 0)
				{
					var pair = end + 1 < Source.Length ? (Source[end], Source[end + 1]) : default;
					depth += pair switch
					{
						('/', '*') => 1,
						('*', '/') => -1,
						_ => 0
					};
					
					end += pair is ('/', '*') or ('*', '/') ? 2 : 1;
				}
				
				if (depth > 0)
				{
					var opener = new Token(TokenType.Invalid, Source, new TextRange(position, position + 2))
					{
						Error = "Unterminated block comment"
					};
					
					comment = new(opener, end);
					return true;
				}
				
				break;
			
			default:
				comment = default;
				return false;
		}
		
		var range = new TextRange(position, end);
		var token = new Token(tokenType, Source, range);
		comment = new(token, end);
		return true;
	}
	
	private bool TryScanOperator(int position, out ScanResult op)
	{
		var end = position;
		while (end < Source.Length && end - position < TokenType.LongestOperator)
		{
			var c = Source[end];
			if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '_')
				break;
			
			end++;
		}
		
		// Tries longest matching operator first, then progressively shrinks until matches or fails all
		while (end > position)
		{
			var range = new TextRange(position, end);
			var operatorString = new string(Source.GetText(range));
			if (TokenType.GetOperator(operatorString) is { } opType)
			{
				var token = new Token(opType, Source, range);
				op = new ScanResult(token, end);
				return true;
			}
			
			end--;
		}
		
		op = default;
		return false;
	}
	
	private ScanResult ScanString(int position)
	{
		var line = Source.GetLineColumn(position).Line;
		var segments = new List<string>();
		var holes = new List<InterpolationHole>();
		var isValid = true;
		var segmentStart = position + 1;
		var escaped = false;
		var end = segmentStart;
		
		while (end < Source.Length && Source[end] is not ('\n' or '\r'))
		{
			var character = Source[end];
			if (character == '"' && !escaped)
			{
				isValid &= AddSegment(segments, segmentStart, end);
				return CreateStringToken(new TextRange(position, end + 1), segments, holes, isValid);
			}
			
			if (character == '{' && !escaped)
			{
				isValid &= AddSegment(segments, segmentStart, end);
				var (hole, next, isValidSpec) = ScanHole(end + 1, line);
				end = next;
				if (hole is null)
					break;
				
				isValid &= isValidSpec;
				holes.Add(hole.Value);
				segmentStart = next;
				continue;
			}
			
			escaped = character == '\\' && !escaped;
			end++;
		}
		
		var invalidToken = new Token(TokenType.Invalid, Source, new TextRange(position, end))
		{
			Error = "Unterminated string literal"
		};
		
		return new ScanResult(invalidToken, end);
	}
	
	private bool AddSegment(List<string> segments, int start, int end)
	{
		var (text, isValid) = UnescapeString(Source.GetText(new TextRange(start, end)), true);
		segments.Add(text);
		return isValid;
	}
	
	private (InterpolationHole? Hole, int End, bool IsValid) ScanHole(int position, int line)
	{
		var tokens = new List<Token>();
		var braces = 0;
		var brackets = 0;
		while (true)
		{
			var (token, next) = ScanToken(position);
			if (next <= position || token.Type is TokenType.EndOfFile or TokenType.Newline || token.Line != line)
				return (null, position, true);
			
			position = next;
			if (token.Type.IsFiltered)
				continue;
			
			switch (token.Type)
			{
				case TokenType.OpCloseBrace when braces == 0:
					return (new InterpolationHole([..tokens], token.SourceLocation, "", SourceLocation.None), position,
						true);
				
				case TokenType.OpColon when braces == 0 && brackets == 0:
					return ScanSpec([..tokens], token.SourceLocation, position);
				
				case TokenType.OpOpenBrace:
					braces++;
					break;
				
				case TokenType.OpCloseBrace:
					braces--;
					break;
				
				case TokenType.OpOpenParen or TokenType.OpOpenBracket:
					brackets++;
					break;
				
				case TokenType.OpCloseParen or TokenType.OpCloseBracket when brackets > 0:
					brackets--;
					break;
			}
			
			tokens.Add(token);
		}
	}
	
	private (InterpolationHole? Hole, int End, bool IsValid) ScanSpec(ImmutableArray<Token> tokens,
		SourceLocation colon, int position)
	{
		var start = position;
		var escaped = false;
		while (position < Source.Length && Source[position] is not ('\n' or '\r'))
		{
			var character = Source[position];
			if (character == '}' && !escaped)
			{
				var range = new TextRange(start, position);
				var (spec, isValid) = UnescapeString(Source.GetText(range), true);
				return (new InterpolationHole(tokens, colon, spec, new SourceLocation(Source, range)), position + 1,
					isValid);
			}
			
			escaped = character == '\\' && !escaped;
			position++;
		}
		
		return (null, position, true);
	}
	
	private ScanResult CreateStringToken(TextRange range, List<string> segments, List<InterpolationHole> holes,
		bool isValid)
	{
		if (!isValid)
		{
			var invalidToken = new Token(TokenType.Invalid, Source, range)
			{
				Error = "Invalid escape sequence in string literal"
			};
			
			return new ScanResult(invalidToken, range.End);
		}
		
		var suffixEnd = ScanSuffix(range.End);
		if (holes.Count == 0)
			return CreateSuffixedToken(TokenType.StringLiteral, range.Start, range.End, suffixEnd, segments[0]);
		
		if (suffixEnd > range.End)
		{
			var invalidToken = new Token(TokenType.Invalid, Source, range with { End = suffixEnd })
			{
				Error = $"Cannot use '{Source.GetText(new TextRange(range.End, suffixEnd))}' on interpolated strings"
			};
			
			return new ScanResult(invalidToken, suffixEnd);
		}
		
		var interpolatedToken = new Token(TokenType.InterpolatedStringLiteral, Source, range)
		{
			Interpolation = new([..segments], [..holes])
		};
		
		return new ScanResult(interpolatedToken, range.End);
	}
	
	/// <summary>
	/// Unescapes a string by replacing escape sequences with their respective literal values
	/// </summary>
	/// <param name="text">The string to unescape</param>
	/// <param name="escapeOpenBrace">Whether to allow \{ as a valid escape character</param>
	/// <returns>A tuple: (result, valid)</returns>
	public static (string Result, bool IsValid) UnescapeString(ReadOnlySpan<char> text, bool escapeOpenBrace = false)
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
					escaped = false;
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
					if (i + utf16Length >= text.Length || !TryAppendCodePoint(result, text.Slice(i + 1, utf16Length)))
						valid = false;
					
					i += utf16Length;
					break;
				
				case 'U':
					const int utf32Length = 8;
					if (i + utf32Length >= text.Length || !TryAppendCodePoint(result, text.Slice(i + 1, utf32Length)))
						valid = false;
					
					i += utf32Length;
					break;
			}
		}
		
		return (result.ToString(), valid);
	}
	
	private static bool TryAppendCodePoint(StringBuilder result, ReadOnlySpan<char> hexDigits)
	{
		if (!uint.TryParse(hexDigits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint)
		    || !Rune.IsValid(codePoint))
			return false;
		
		result.Append(char.ConvertFromUtf32((int)codePoint));
		return true;
	}
	
	private ScanResult ScanChar(int position)
	{
		var end = position + 1;
		var isValid = false;
		var escaped = false;
		
		while (end < Source.Length)
		{
			if (Source[end] == '\'' && !escaped)
			{
				end++;
				isValid = true;
				break;
			}
			
			if (Source[end] is '\n' or '\r')
				break;
			
			if (Source[end] == '\\')
				escaped = !escaped;
			else
				escaped = false;
			
			end++;
		}
		
		if (!isValid)
		{
			var invalidToken = new Token(TokenType.InvalidCharLiteral, Source, new TextRange(position, end));
			return new ScanResult(invalidToken, end);
		}
		
		var value = UnescapeString(Source.GetText(new TextRange(position + 1, end - 1)));
		if (!value.IsValid || !IsSingleRune(value.Result))
		{
			var invalidToken = new Token(TokenType.InvalidCharLiteral, Source, new TextRange(position, end));
			return new ScanResult(invalidToken, end);
		}
		
		return CreateSuffixedToken(TokenType.CharLiteral, position, end, ScanSuffix(end), value.Result);
	}
	
	private static bool IsSingleRune(string text) =>
		Rune.DecodeFromUtf16(text, out _, out var length) == OperationStatus.Done && length == text.Length;
}