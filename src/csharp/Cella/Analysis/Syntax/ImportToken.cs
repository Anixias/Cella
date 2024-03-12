using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public readonly struct ImportToken
{
	public readonly Token identifier;
	public readonly Token? alias;

	public ImportToken(Token identifier, Token? alias)
	{
		this.identifier = identifier;
		this.alias = alias;
	}
}