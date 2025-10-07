namespace Cella.Core.Syntax;

public interface IParser<out T>
{
	T? Parse(ref int index);
	T? Parse();
}