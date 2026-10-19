namespace Cella.Core.Text;

public interface ISource
{
	string FilePath { get; }
	string Name { get; }
	char this[int position] { get; }
	int Length { get; }
	ReadOnlySpan<char> GetText(TextRange range);
	(int Line, int Column) GetLineColumn(int position);
	TextRange GetLineRange(int line);
}