namespace Cella.Analysis.Text;

public interface IBuffer
{
	char this[int position] { get; }
	int Length { get; }
	string GetText();
	string GetText(int line);
	string GetText(TextRange range);
	(int line, int column) GetLineColumn(int position);
	TextRange GetLineRange(int line);
}