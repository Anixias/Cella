namespace Cella.Core.Text;

public interface ISource
{
	char this[int position] { get; }
	int Length { get; }
	ReadOnlySpan<char> GetText();
	ReadOnlySpan<char> GetText(int line);
	ReadOnlySpan<char> GetText(TextRange range);
	(int line, int column) GetLineColumn(int position);
	TextRange GetLineRange(int line);
}