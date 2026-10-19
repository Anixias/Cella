using Cella.Core.Text;

namespace Cella.Compiler.Projects;

public sealed class SettingKeys
{
	private readonly string _text;
	private readonly List<TextRange> _settings = [];
	private int position;
	
	private SettingKeys(string text) => _text = text;
	
	public static IReadOnlyList<TextRange> Find(string text)
	{
		var keys = new SettingKeys(text);
		keys.ScanDocument();
		return keys._settings;
	}
	
	private bool AtEnd => position >= _text.Length;
	
	private char Current => _text[position];
	
	private void ScanDocument()
	{
		List<string> table = [];
		while (true)
		{
			SkipSpace(true);
			if (AtEnd)
				return;
			
			if (Current == '[')
			{
				var isArray = Follows("[[");
				position += isArray ? 2 : 1;
				table = ScanKey([]);
				position += Follows(isArray ? "]]" : "]") ? isArray ? 2 : 1 : 0;
			}
			else
			{
				var start = position;
				var key = ScanKey(table);
				SkipSpace(false);
				if (!AtEnd && Current == '=')
				{
					position++;
					ScanValue(key);
				}
				else if (position == start)
					position++;
			}
			
			SkipLine();
		}
	}
	
	private List<string> ScanKey(List<string> prefix)
	{
		var path = new List<string>(prefix);
		while (true)
		{
			SkipSpace(false);
			var start = position;
			if (ScanKeyPart() is not { } part)
				return path;
			
			path.Add(part);
			if (CellaProject.IsSettingPath(path))
				_settings.Add(new TextRange(start, position));
			
			SkipSpace(false);
			if (AtEnd || Current != '.')
				return path;
			
			position++;
		}
	}
	
	private string? ScanKeyPart()
	{
		if (AtEnd)
			return null;
		
		if (Current is '"' or '\'')
		{
			var quote = Current;
			var start = ++position;
			while (!AtEnd && Current != quote && Current != '\n')
				position += Current == '\\' && quote == '"' ? 2 : 1;
			
			var end = Math.Min(position, _text.Length);
			if (!AtEnd && Current == quote)
				position++;
			
			return _text[start..end];
		}
		
		var bare = position;
		while (!AtEnd && (char.IsAsciiLetterOrDigit(Current) || Current is '_' or '-'))
			position++;
		
		return position > bare ? _text[bare..position] : null;
	}
	
	private void ScanValue(List<string> path)
	{
		SkipSpace(false);
		if (AtEnd)
			return;
		
		if (Follows("\"\"\"") || Follows("'''"))
		{
			var closer = _text.Substring(position, 3);
			position += 3;
			while (!AtEnd && !Follows(closer))
				position += Current == '\\' && closer[0] == '"' ? 2 : 1;
			
			position = Math.Min(position + 3, _text.Length);
			return;
		}
		
		if (Current is '"' or '\'')
		{
			ScanKeyPart();
			return;
		}
		
		if (Current == '[')
		{
			position++;
			while (true)
			{
				SkipSpace(true);
				if (AtEnd || Current == ']')
					break;
				
				var start = position;
				ScanValue(path);
				SkipSpace(true);
				if (!AtEnd && Current == ',')
					position++;
				else if (position == start)
					position++;
			}
			
			position = Math.Min(position + 1, _text.Length);
			return;
		}
		
		if (Current == '{')
		{
			position++;
			while (true)
			{
				SkipSpace(false);
				if (AtEnd || Current is '}' or '\n')
					break;
				
				var start = position;
				var key = ScanKey(path);
				SkipSpace(false);
				if (!AtEnd && Current == '=')
				{
					position++;
					ScanValue(key);
				}
				
				SkipSpace(false);
				if (!AtEnd && Current == ',')
					position++;
				else if (position == start)
					position++;
			}
			
			if (!AtEnd && Current == '}')
				position++;
			
			return;
		}
		
		while (!AtEnd && Current is not (',' or ']' or '}' or '\n' or '#'))
			position++;
	}
	
	private void SkipSpace(bool newlines)
	{
		while (!AtEnd)
		{
			if (Current == '#')
				SkipComment();
			else if (Current is ' ' or '\t' or '\r' || newlines && Current == '\n')
				position++;
			else
				return;
		}
	}
	
	private void SkipComment()
	{
		while (!AtEnd && Current != '\n')
			position++;
	}
	
	private void SkipLine()
	{
		SkipSpace(false);
		while (!AtEnd && Current != '\n')
			position++;
	}
	
	private bool Follows(string text) => string.CompareOrdinal(_text, position, text, 0, text.Length) == 0;
}